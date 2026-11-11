using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Delivery.Dsn;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Delivery.Tests;

/// <summary>
/// The per-recipient bookkeeping of <see cref="DeliveryWorker.ProcessMessageAsync"/>: partial success is recorded
/// and never re-sent (H1), the outcome survives transient database errors and a lost one is recovered by the lease
/// sweep (H2), the suppression policy (T1075), system-mail exemption, and DSN generation (T1074).
/// </summary>
[TestClass]
public class DeliveryWorkerOutcomeTests
{
    private const int Tenant = TenantDefaults.DefaultTenantId;
    private const string Sender = "sender@tenant.test";

    private SqliteConnection _connection = null!;
    private DbContextOptions<RelayDbContext> _dbOptions = null!;
    private ServiceProvider _provider = null!;
    private ScriptedDelivery _delivery = null!;
    private int _completeFailures;

    [TestInitialize]
    public async Task Setup()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<RelayDbContext>().UseSqlite(_connection).Options;
        await using (var schema = NewContext())
        {
            await schema.Database.EnsureCreatedAsync();
            schema.AcceptedSenderDomains.Add(new AcceptedSenderDomain { TenantId = Tenant, Domain = "tenant.test" });
            await schema.SaveChangesAsync();
        }

        _delivery = new ScriptedDelivery();
        var services = new ServiceCollection();
        services.AddScoped<RelayDbContext>(_ => NewContext());
        services.AddScoped<IMessageQueue>(sp => new FlakyQueue(
            new MessageQueue(sp.GetRequiredService<RelayDbContext>(), new CurrentActor(), new AdminAuditService(sp.GetRequiredService<RelayDbContext>())),
            () => Interlocked.Decrement(ref _completeFailures) >= 0));
        services.AddScoped<ISuppressionService>(sp => new SuppressionService(sp.GetRequiredService<RelayDbContext>(),
            new CurrentActor(), new AdminAuditService(sp.GetRequiredService<RelayDbContext>())));
        services.AddScoped<IDataRetentionSettingsService>(sp => new DataRetentionSettingsService(sp.GetRequiredService<RelayDbContext>()));
        services.AddScoped<IDeliveryService>(_ => _delivery);
        services.AddScoped<IDsnService>(sp => new DsnService(
            sp.GetRequiredService<RelayDbContext>(), sp.GetRequiredService<IMessageQueue>(),
            sp.GetRequiredService<ISuppressionService>(), new FixedDnsSettings(), NullLogger<DsnService>.Instance));
        _provider = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private RelayDbContext NewContext() => new(_dbOptions, new CurrentTenant());

    private DeliveryWorker CreateWorker(DeliveryOptions? options = null) => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        Substitute.For<IActivityNotifier>(),
        Options.Create(options ?? new DeliveryOptions()),
        new StubRuntimeConfigCache(),
        NullLogger<DeliveryWorker>.Instance);

    private async Task<QueuedMessage> EnqueueAsync(string recipients, string sender = Sender, MessageOrigin origin = MessageOrigin.Submitted)
    {
        var raw = Encoding.ASCII.GetBytes($"From: <{sender}>\r\nTo: <{recipients.Split(';')[0]}>\r\nSubject: Quarterly figures\r\nMessage-ID: <q1@tenant.test>\r\n\r\nBody\r\n");
        var msg = new QueuedMessage
        {
            TenantId = Tenant,
            MessageId = "<q1@tenant.test>",
            Sender = sender,
            Recipients = recipients,
            RawMessage = raw,
            SizeBytes = raw.Length,
            Origin = origin,
        };
        await using var ctx = NewContext();
        ctx.QueuedMessages.Add(msg);
        await ctx.SaveChangesAsync();
        return await ClaimAsync(msg.Id);
    }

    /// <summary>What the poll loop does before ProcessMessageAsync: claim (Delivering + lease) and load with body.</summary>
    private async Task<QueuedMessage> ClaimAsync(long id, TimeSpan? lease = null)
    {
        await using var ctx = NewContext();
        await ctx.QueuedMessages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.NextRetryUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var queue = new MessageQueue(ctx, new CurrentActor(), new AdminAuditService(ctx));
        return (await queue.TryClaimAsync(id, DateTimeOffset.UtcNow + (lease ?? DeliveryWorker.LeaseDuration)))!;
    }

    private async Task<QueuedMessage> LoadAsync(long id)
    {
        await using var ctx = NewContext();
        return await ctx.QueuedMessages.AsNoTracking().FirstAsync(m => m.Id == id);
    }

    private async Task<bool> IsSuppressedAsync(string address)
    {
        await using var ctx = NewContext();
        return await new SuppressionService(ctx, new CurrentActor(), new AdminAuditService(ctx)).IsSuppressedAsync(address, Tenant);
    }

    private async Task<List<QueuedMessage>> DsnsAsync()
    {
        await using var ctx = NewContext();
        return await ctx.QueuedMessages.AsNoTracking().Where(m => m.Origin == MessageOrigin.DeliveryStatusNotification).ToListAsync();
    }

    private static DeliveryResult Result(string recipient, string code, string text = "", bool rcptRejected = false, bool viaSmartHost = false) =>
        new() { Recipient = recipient, StatusCode = code, StatusMessage = text, RemoteServer = "mx.a.test:25", RecipientRejected = rcptRejected, ViaSmartHost = viaSmartHost };

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PartialFailure_RecordsDeliveredRecipients_RetrySendsOnlyTheRest()
    {
        // H1: a.test accepted, b.test was down. The retry must not send to a.test again.
        _delivery.Script = m => [Result("x@a.test", "250", "OK"), Result("y@b.test", "451", "4.4.1 connection refused")];
        var message = await EnqueueAsync("x@a.test;y@b.test");
        var worker = CreateWorker();

        await worker.ProcessMessageAsync(message, CancellationToken.None);

        var stored = await LoadAsync(message.Id);
        Assert.AreEqual(MessageStatus.Queued, stored.Status);
        Assert.AreEqual("x@a.test", stored.DeliveredRecipients);
        Assert.AreEqual(1, stored.RetryCount);
        Assert.IsNull(stored.LeaseExpiresUtc);

        _delivery.Script = m => [.. m.Recipients.Split(';').Select(r => Result(r, "250", "OK"))];
        await worker.ProcessMessageAsync(await ClaimAsync(message.Id), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "y@b.test" }, _delivery.Calls[^1]);
        stored = await LoadAsync(message.Id);
        Assert.AreEqual(MessageStatus.Delivered, stored.Status);
        Assert.AreEqual("x@a.test;y@b.test", stored.DeliveredRecipients);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PermanentFailure_IsFinal_NotRetried_AndBouncesWithDsn()
    {
        _delivery.Script = m => [Result("dead@a.test", "550", "5.1.1 <dead@a.test>: user unknown", rcptRejected: true), Result("y@b.test", "451", "4.4.1 timeout")];
        var message = await EnqueueAsync("dead@a.test;y@b.test");
        var worker = CreateWorker();

        await worker.ProcessMessageAsync(message, CancellationToken.None);
        var stored = await LoadAsync(message.Id);
        Assert.AreEqual(MessageStatus.Queued, stored.Status);
        Assert.AreEqual("dead@a.test", stored.FailedRecipients);
        Assert.IsTrue(await IsSuppressedAsync("dead@a.test"), "5.1.1 at RCPT from the recipient's MX suppresses");

        _delivery.Script = m => [.. m.Recipients.Split(';').Select(r => Result(r, "250", "OK"))];
        await worker.ProcessMessageAsync(await ClaimAsync(message.Id), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "y@b.test" }, _delivery.Calls[^1], "a permanently failed recipient is not retried");
        stored = await LoadAsync(message.Id);
        Assert.AreEqual(MessageStatus.Bounced, stored.Status);

        var dsn = (await DsnsAsync()).Single();
        Assert.AreEqual("", dsn.Sender, "a DSN has a null return path");
        Assert.AreEqual(Sender, dsn.Recipients);
        var body = Encoding.UTF8.GetString(dsn.RawMessage);
        StringAssert.Contains(body, "multipart/report");
        StringAssert.Contains(body, "Final-Recipient: rfc822; dead@a.test");
        StringAssert.Contains(body, "Status: 5.1.1");
        Assert.IsFalse(body.Contains("Final-Recipient: rfc822; y@b.test"), "delivered recipients are not reported");
        StringAssert.Contains(body, "Subject: Quarterly figures");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PolicyRejection_DoesNotSuppress()
    {
        _delivery.Script = m => [Result("x@a.test", "550", "5.7.1 Service unavailable; client host blocked using Spamhaus", rcptRejected: true)];
        var message = await EnqueueAsync("x@a.test");

        await CreateWorker().ProcessMessageAsync(message, CancellationToken.None);

        Assert.IsFalse(await IsSuppressedAsync("x@a.test"));
        Assert.AreEqual(MessageStatus.Bounced, (await LoadAsync(message.Id)).Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SmartHostRejection_DoesNotSuppress()
    {
        _delivery.Script = m => [Result("x@a.test", "550", "5.1.1 user unknown", rcptRejected: true, viaSmartHost: true)];
        var message = await EnqueueAsync("x@a.test");

        await CreateWorker().ProcessMessageAsync(message, CancellationToken.None);

        Assert.IsFalse(await IsSuppressedAsync("x@a.test"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SystemMail_IgnoresSuppression_AndNeverSuppresses()
    {
        await using (var ctx = NewContext())
            await new SuppressionService(ctx, new CurrentActor(), new AdminAuditService(ctx))
                .AddAsync("reports@a.test", SuppressionReason.HardBounce, "550 5.1.1", Tenant);

        _delivery.Script = m => [.. m.Recipients.Split(';').Select(r => Result(r, "250", "OK"))];
        var report = await EnqueueAsync("reports@a.test", origin: MessageOrigin.System);
        await CreateWorker().ProcessMessageAsync(report, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "reports@a.test" }, _delivery.Calls[^1], "a report reaches a suppressed report recipient");

        _delivery.Script = m => [Result("ops@a.test", "550", "5.1.1 user unknown", rcptRejected: true)];
        var alert = await EnqueueAsync("ops@a.test", origin: MessageOrigin.System);
        await CreateWorker().ProcessMessageAsync(alert, CancellationToken.None);
        Assert.IsFalse(await IsSuppressedAsync("ops@a.test"), "a bounce of system mail never suppresses");
        Assert.AreEqual(0, (await DsnsAsync()).Count, "system mail is never answered with a DSN");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Dsn_NotSentToForeignOrNullSender_NorWhenDisabled_NorToSuppressedSender()
    {
        _delivery.Script = m => [.. m.Recipients.Split(';').Select(r => Result(r, "550", "5.1.1 user unknown", rcptRejected: true))];
        var worker = CreateWorker();

        // Forged sender: its domain is not the tenant's → no backscatter.
        await worker.ProcessMessageAsync(await EnqueueAsync("a1@a.test", sender: "victim@elsewhere.test"), CancellationToken.None);
        // Null sender (an inbound bounce): never answered.
        await worker.ProcessMessageAsync(await EnqueueAsync("a2@a.test", sender: ""), CancellationToken.None);
        Assert.AreEqual(0, (await DsnsAsync()).Count);

        // Sender suppressed.
        await using (var ctx = NewContext())
            await new SuppressionService(ctx, new CurrentActor(), new AdminAuditService(ctx))
                .AddAsync("blocked@tenant.test", SuppressionReason.Complaint, null, Tenant);
        await worker.ProcessMessageAsync(await EnqueueAsync("a3@a.test", sender: "blocked@tenant.test"), CancellationToken.None);
        Assert.AreEqual(0, (await DsnsAsync()).Count);

        // Tenant has DSNs off.
        await using (var ctx = NewContext())
            await ctx.Tenants.Where(t => t.Id == Tenant).ExecuteUpdateAsync(s => s.SetProperty(t => t.DsnEnabled, false));
        await worker.ProcessMessageAsync(await EnqueueAsync("a4@a.test"), CancellationToken.None);
        Assert.AreEqual(0, (await DsnsAsync()).Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task AllRecipientsSuppressed_SendsDsnForThem()
    {
        await using (var ctx = NewContext())
            await new SuppressionService(ctx, new CurrentActor(), new AdminAuditService(ctx))
                .AddAsync("gone@a.test", SuppressionReason.HardBounce, "550 5.1.1", Tenant);
        var message = await EnqueueAsync("gone@a.test");

        await CreateWorker().ProcessMessageAsync(message, CancellationToken.None);

        Assert.AreEqual(MessageStatus.Suppressed, (await LoadAsync(message.Id)).Status);
        Assert.AreEqual(0, _delivery.Calls.Count);
        var body = Encoding.UTF8.GetString((await DsnsAsync()).Single().RawMessage);
        StringAssert.Contains(body, "Final-Recipient: rfc822; gone@a.test");
        StringAssert.Contains(body, "suppression list");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RetryWindowExhausted_BouncesTheTemporaryFailures()
    {
        _delivery.Script = m => [Result("x@a.test", "451", "4.4.1 timeout")];
        var message = await EnqueueAsync("x@a.test");

        await CreateWorker(new DeliveryOptions { MaxRetryHours = 0, RetryIntervalsMinutes = [1] }).ProcessMessageAsync(message, CancellationToken.None);

        var stored = await LoadAsync(message.Id);
        Assert.AreEqual(MessageStatus.Bounced, stored.Status);
        Assert.AreEqual("x@a.test", stored.FailedRecipients);
        StringAssert.Contains(Encoding.UTF8.GetString((await DsnsAsync()).Single().RawMessage), "Status: 4.4.1");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EhloNotConfigured_CountsAsTemporaryFailure()
    {
        // A configuration gap no longer holds mail for ever: it is retried within the normal window.
        _delivery.Script = _ => throw new EhloNotConfiguredException();
        var message = await EnqueueAsync("x@a.test");

        await CreateWorker().ProcessMessageAsync(message, CancellationToken.None);

        var stored = await LoadAsync(message.Id);
        Assert.AreEqual(MessageStatus.Queued, stored.Status);
        Assert.AreEqual(1, stored.RetryCount);
        StringAssert.Contains(stored.LastError, "4.3.5");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OutcomeIsPersisted_DespiteTransientDatabaseErrors()
    {
        // H2: the first two writes of the outcome fail; the retry still records the delivery.
        _completeFailures = 2;
        _delivery.Script = m => [Result("x@a.test", "250", "OK")];
        var message = await EnqueueAsync("x@a.test");

        await CreateWorker().ProcessMessageAsync(message, CancellationToken.None);

        var stored = await LoadAsync(message.Id);
        Assert.AreEqual(MessageStatus.Delivered, stored.Status);
        Assert.AreEqual("x@a.test", stored.DeliveredRecipients);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LostBookkeeping_LeaseSweepReturnsMessageToQueue()
    {
        // H2 crash simulation: the outcome can never be written (the process "dies" between send and bookkeeping).
        // The row stays Delivering with its lease; it is not lost — once the lease expires the sweep re-queues it.
        _completeFailures = int.MaxValue;
        _delivery.Script = m => [Result("x@a.test", "250", "OK")];
        var message = await EnqueueAsync("x@a.test");

        await CreateWorker().ProcessMessageAsync(message, CancellationToken.None);
        Assert.AreEqual(MessageStatus.Delivering, (await LoadAsync(message.Id)).Status);

        await using var ctx = NewContext();
        var queue = new MessageQueue(ctx, new CurrentActor(), new AdminAuditService(ctx));
        Assert.AreEqual(0, await queue.RequeueExpiredLeasesAsync(DateTimeOffset.UtcNow, []), "lease not expired yet");
        Assert.AreEqual(0, await queue.RequeueExpiredLeasesAsync(DateTimeOffset.UtcNow.AddHours(1), [message.Id]), "in-flight messages are never swept");
        Assert.AreEqual(1, await queue.RequeueExpiredLeasesAsync(DateTimeOffset.UtcNow.AddHours(1), []));
        var stored = await LoadAsync(message.Id);
        Assert.AreEqual(MessageStatus.Queued, stored.Status);
        Assert.IsNull(stored.LeaseExpiresUtc);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ShutdownBeforeTheSend_RequeuesWithoutPenalty()
    {
        var message = await EnqueueAsync("x@a.test");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _delivery.Script = m => throw new OperationCanceledException(cts.Token);

        await CreateWorker().ProcessMessageAsync(message, cts.Token);

        var stored = await LoadAsync(message.Id);
        Assert.AreEqual(MessageStatus.Queued, stored.Status);
        Assert.AreEqual(0, stored.RetryCount);
        await using var ctx = NewContext();
        Assert.AreEqual(0, await ctx.DeliveryLogs.CountAsync(), "an interrupted attempt is not logged as a failure");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task DeliveryException_FromOtherImplementations_IsStillHonoured()
    {
        _delivery.Script = _ => throw new DeliveryException("failed", [Result("x@a.test", "250", "OK"), Result("y@b.test", "451", "4.4.1")]);
        var message = await EnqueueAsync("x@a.test;y@b.test");

        await CreateWorker().ProcessMessageAsync(message, CancellationToken.None);

        Assert.AreEqual("x@a.test", (await LoadAsync(message.Id)).DeliveredRecipients);
    }

    private sealed class ScriptedDelivery : IDeliveryService
    {
        public List<string[]> Calls { get; } = [];
        public Func<QueuedMessage, IReadOnlyList<DeliveryResult>> Script { get; set; } = _ => [];

        public Task<IReadOnlyList<DeliveryResult>> DeliverAsync(QueuedMessage message, CancellationToken cancellationToken = default)
        {
            Calls.Add(message.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries));
            return Task.FromResult(Script(message));
        }
    }

    private sealed class FixedDnsSettings : IDnsSettingsService
    {
        public Task<DnsSettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new DnsSettings { PublicHostname = "relay.example.com" });
        public Task UpdateAsync(DnsSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>The real queue, except that <see cref="CompleteAttemptAsync"/> fails while <c>shouldFail</c> says so.</summary>
    private sealed class FlakyQueue(MessageQueue inner, Func<bool> shouldFail) : IMessageQueue
    {
        public Task CompleteAttemptAsync(long messageId, DeliveryAttemptOutcome outcome, CancellationToken cancellationToken = default) =>
            shouldFail() ? throw new DbUpdateException("simulated: database is locked") : inner.CompleteAttemptAsync(messageId, outcome, cancellationToken);

        public Task<long> EnqueueAsync(QueuedMessage message, CancellationToken cancellationToken = default) => inner.EnqueueAsync(message, cancellationToken);
        public Task<IReadOnlyList<QueuedMessage>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default) => inner.GetPendingAsync(maxCount, cancellationToken);
        public Task UpdateStatusAsync(long messageId, MessageStatus status, string? error = null, CancellationToken cancellationToken = default) => inner.UpdateStatusAsync(messageId, status, error, cancellationToken);
        public Task<QueuedMessage?> GetByIdAsync(long messageId, CancellationToken cancellationToken = default) => inner.GetByIdAsync(messageId, cancellationToken);
        public Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default) => inner.GetQueueDepthAsync(cancellationToken);
        public Task SetRetryAsync(long messageId, int retryCount, DateTimeOffset nextRetryUtc, CancellationToken cancellationToken = default) => inner.SetRetryAsync(messageId, retryCount, nextRetryUtc, cancellationToken);
        public Task<bool> RequeueAsync(long messageId, CancellationToken cancellationToken = default) => inner.RequeueAsync(messageId, cancellationToken);
        public Task DeleteAsync(long messageId, CancellationToken cancellationToken = default) => inner.DeleteAsync(messageId, cancellationToken);
        public Task<IReadOnlyList<QueuedMessage>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default) => inner.GetRecentAsync(maxCount, cancellationToken);
        public Task<IReadOnlyList<QueuedMessage>> GetNonDeliveredAsync(int maxCount, CancellationToken cancellationToken = default) => inner.GetNonDeliveredAsync(maxCount, cancellationToken);
        public Task<IReadOnlyList<QueuedMessageSummary>> GetSummariesAsync(int maxCount, bool nonDeliveredOnly, CancellationToken cancellationToken = default) => inner.GetSummariesAsync(maxCount, nonDeliveredOnly, cancellationToken);
        public Task<int> RequeueStaleDeliveringAsync(CancellationToken cancellationToken = default) => inner.RequeueStaleDeliveringAsync(cancellationToken);
        public Task<int> RequeueExpiredLeasesAsync(DateTimeOffset nowUtc, IReadOnlyCollection<long> inFlightIds, CancellationToken cancellationToken = default) => inner.RequeueExpiredLeasesAsync(nowUtc, inFlightIds, cancellationToken);
        public Task<IReadOnlyList<int>> GetDueTenantsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => inner.GetDueTenantsAsync(nowUtc, cancellationToken);
        public Task<IReadOnlyList<QueueCandidate>> GetDueCandidatesAsync(int tenantId, DateTimeOffset nowUtc, int maxCount, CancellationToken cancellationToken = default) => inner.GetDueCandidatesAsync(tenantId, nowUtc, maxCount, cancellationToken);
        public Task<QueuedMessage?> TryClaimAsync(long messageId, DateTimeOffset leaseExpiresUtc, CancellationToken cancellationToken = default) => inner.TryClaimAsync(messageId, leaseExpiresUtc, cancellationToken);
        public Task SetDeliveredRecipientsAsync(long messageId, string deliveredRecipients, CancellationToken cancellationToken = default) => inner.SetDeliveredRecipientsAsync(messageId, deliveredRecipients, cancellationToken);
        public Task StripBodyAsync(long messageId, CancellationToken cancellationToken = default) => inner.StripBodyAsync(messageId, cancellationToken);
    }
}

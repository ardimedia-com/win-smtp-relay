using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Delivery;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Delivery.Tests;

/// <summary>
/// Tests the suppression enforcement in <see cref="DeliveryWorker.ProcessMessageAsync"/> — the path that
/// must never deliver to addresses on a tenant's suppression list, must mark an all-suppressed message
/// terminal, and must auto-suppress recipients that get a permanent (5xx) failure.
///
/// The worker resolves IMessageQueue / IDeliveryService / RelayDbContext / ISuppressionService from an
/// IServiceScopeFactory, so the harness builds a ServiceCollection over a single in-memory SQLite
/// connection (real MessageQueue + SuppressionService) and a controllable stub IDeliveryService. Each
/// DI scope gets its own RelayDbContext over the shared connection — mirroring production, where the
/// worker disposes a scope (and its context) per message; the in-memory data lives on the connection,
/// which the test keeps open for its lifetime.
/// </summary>
[TestClass]
public class DeliveryWorkerSuppressionTests
{
    private const int Tenant = TenantDefaults.DefaultTenantId;

    private SqliteConnection _connection = null!;
    private DbContextOptions<RelayDbContext> _dbOptions = null!;
    private ServiceProvider _provider = null!;
    private StubDeliveryService _delivery = null!;

    [TestInitialize]
    public void Setup()
    {
        // A single open in-memory SQLite connection holds the database; every context built over it
        // (across DI scopes and test helpers) sees the same data. ":memory:" is per-connection, so the
        // connection must stay open for the whole test.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var schema = NewContext())
            schema.Database.EnsureCreated();

        _delivery = new StubDeliveryService();

        var services = new ServiceCollection();
        // A fresh context per scope (like production); the DI container disposes it with the scope, but
        // the shared connection — and therefore the data — survives.
        services.AddScoped<RelayDbContext>(_ => NewContext());
        services.AddScoped<IMessageQueue>(sp => new MessageQueue(sp.GetRequiredService<RelayDbContext>()));
        services.AddScoped<ISuppressionService>(sp => new SuppressionService(sp.GetRequiredService<RelayDbContext>()));
        // Seeded DataRetentionSettings (StripBodyOnDelivery=true, ResendRetentionDays=7) drives the
        // body-retention behaviour exercised below.
        services.AddScoped<IDataRetentionSettingsService>(sp => new DataRetentionSettingsService(sp.GetRequiredService<RelayDbContext>()));
        services.AddScoped<IDeliveryService>(_ => _delivery);
        // No IMessageFilter registered → GetServices returns empty → filters are a no-op.
        _provider = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private RelayDbContext NewContext()
    {
        var current = new CurrentTenant();
        current.SetTenant(Tenant);
        return new RelayDbContext(_dbOptions, current);
    }

    private DeliveryWorker CreateWorker()
    {
        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        return new DeliveryWorker(
            scopeFactory,
            Substitute.For<IActivityNotifier>(),
            Options.Create(new DeliveryOptions()),
            new StubRuntimeConfigCache(),
            NullLogger<DeliveryWorker>.Instance);
    }

    private async Task<QueuedMessage> EnqueueAsync(string recipients)
    {
        var msg = new QueuedMessage
        {
            TenantId = Tenant,
            MessageId = $"<{Guid.NewGuid():N}@test>",
            Sender = "sender@test.com",
            Recipients = recipients,
            RawMessage = [1, 2, 3],
            Status = MessageStatus.Delivering // the poll loop marks Delivering before ProcessMessageAsync
        };
        await using var ctx = NewContext();
        await new MessageQueue(ctx).EnqueueAsync(msg);
        return msg;
    }

    private async Task SuppressAsync(string address, SuppressionReason reason)
    {
        await using var ctx = NewContext();
        await new SuppressionService(ctx).AddAsync(address, reason, null, Tenant);
    }

    private async Task<MessageStatus> StatusOfAsync(long id)
    {
        await using var ctx = NewContext();
        return (await ctx.QueuedMessages.AsNoTracking().FirstAsync(m => m.Id == id)).Status;
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SuppressedRecipient_Skipped_AndLoggedOnce()
    {
        // good@ delivers; bad@ is suppressed → skipped, not delivered, and a single "Suppressed" log row.
        await SuppressAsync("bad@example.com", SuppressionReason.HardBounce);

        var message = await EnqueueAsync("good@example.com;bad@example.com");
        var worker = CreateWorker();

        await worker.ProcessMessageAsync(message, CancellationToken.None);

        // Only the non-suppressed recipient was handed to the delivery service.
        CollectionAssert.AreEquivalent(new[] { "good@example.com" }, _delivery.DeliveredRecipients);

        await using var ctx = NewContext();
        // Exactly one "Suppressed" delivery log was written for the suppressed recipient.
        var suppressedLogs = await ctx.DeliveryLogs.AsNoTracking()
            .Where(l => l.Recipient == "bad@example.com" && l.StatusMessage.Contains("Suppressed"))
            .ToListAsync();
        Assert.AreEqual(1, suppressedLogs.Count);

        // The message itself was delivered (it had a deliverable recipient).
        Assert.AreEqual(MessageStatus.Delivered, await StatusOfAsync(message.Id));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task AllRecipientsSuppressed_MessageSuppressed_NotDelivered()
    {
        await SuppressAsync("a@example.com", SuppressionReason.HardBounce);
        await SuppressAsync("b@example.com", SuppressionReason.Complaint);

        var message = await EnqueueAsync("a@example.com;b@example.com");
        var worker = CreateWorker();

        await worker.ProcessMessageAsync(message, CancellationToken.None);

        Assert.AreEqual(0, _delivery.CallCount, "delivery must not be attempted when all recipients are suppressed");
        Assert.AreEqual(MessageStatus.Suppressed, await StatusOfAsync(message.Id));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NonSuppressedRecipient_DeliveredNormally()
    {
        var message = await EnqueueAsync("ok@example.com");
        var worker = CreateWorker();

        await worker.ProcessMessageAsync(message, CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { "ok@example.com" }, _delivery.DeliveredRecipients);
        Assert.AreEqual(MessageStatus.Delivered, await StatusOfAsync(message.Id));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PermanentFailure_AutoAddsRecipientToSuppressionList()
    {
        // The destination explicitly rejected THIS recipient at RCPT TO with a permanent 5xx
        // (RecipientRejected = true) → the worker must auto-suppress it.
        _delivery.FailWith = _ => new DeliveryException("permanent failure",
        [
            new DeliveryResult
            {
                Recipient = "dead@example.com",
                StatusCode = "550",
                StatusMessage = "mailbox unavailable",
                RemoteServer = "mx.example.com",
                RecipientRejected = true
            }
        ]);

        var message = await EnqueueAsync("dead@example.com");
        var worker = CreateWorker();

        await worker.ProcessMessageAsync(message, CancellationToken.None);

        await using var ctx = NewContext();
        var suppression = new SuppressionService(ctx);
        Assert.IsTrue(await suppression.IsSuppressedAsync("dead@example.com", Tenant),
            "a 5xx-failed recipient must be auto-added to the suppression list");

        // An all-5xx failure is permanent → the message bounces (not re-queued).
        Assert.AreEqual(MessageStatus.Bounced, await StatusOfAsync(message.Id));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task TransactionWide5xx_DoesNotSuppressCoRecipients()
    {
        // Regression guard for the multi-recipient poisoning bug: a single bad address in a same-domain
        // SMTP transaction makes the destination reject the whole transaction, so every recipient is
        // painted with the same 5xx — but NOT flagged RecipientRejected. A transaction-wide 5xx must
        // bounce the message WITHOUT suppressing the (otherwise valid) co-recipients.
        _delivery.FailWith = _ => new DeliveryException("transaction failed",
        [
            new DeliveryResult
            {
                Recipient = "valid@example.com",
                StatusCode = "550",
                StatusMessage = "All MX hosts exhausted for domain example.com: 5.4.1 Access denied",
                RemoteServer = "mx.example.com",
                RecipientRejected = false
            },
            new DeliveryResult
            {
                Recipient = "alsovalid@example.com",
                StatusCode = "550",
                StatusMessage = "All MX hosts exhausted for domain example.com: 5.4.1 Access denied",
                RemoteServer = "mx.example.com",
                RecipientRejected = false
            }
        ]);

        var message = await EnqueueAsync("valid@example.com;alsovalid@example.com");
        var worker = CreateWorker();

        await worker.ProcessMessageAsync(message, CancellationToken.None);

        await using var ctx = NewContext();
        var suppression = new SuppressionService(ctx);
        Assert.IsFalse(await suppression.IsSuppressedAsync("valid@example.com", Tenant),
            "a transaction-wide 5xx must NOT auto-suppress a co-recipient");
        Assert.IsFalse(await suppression.IsSuppressedAsync("alsovalid@example.com", Tenant),
            "a transaction-wide 5xx must NOT auto-suppress a co-recipient");

        // It is still an all-5xx failure → the message bounces.
        Assert.AreEqual(MessageStatus.Bounced, await StatusOfAsync(message.Id));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PartialDelivery_KeepsBodyForResend_AndRecordsDeliveredRecipients()
    {
        // good@ delivers; bad@ is suppressed (undelivered). The message keeps its body so it can be resent.
        await SuppressAsync("bad@example.com", SuppressionReason.HardBounce);

        var message = await EnqueueAsync("good@example.com;bad@example.com");
        var worker = CreateWorker();

        await worker.ProcessMessageAsync(message, CancellationToken.None);

        await using var ctx = NewContext();
        var stored = await ctx.QueuedMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);
        Assert.AreEqual(MessageStatus.Delivered, stored.Status);
        Assert.IsTrue(stored.RawMessage.Length > 0, "a message with an undelivered (suppressed) recipient keeps its body for resend");
        Assert.AreEqual("good@example.com", stored.DeliveredRecipients);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FullDelivery_StripsBody()
    {
        var message = await EnqueueAsync("ok@example.com");
        var worker = CreateWorker();

        await worker.ProcessMessageAsync(message, CancellationToken.None);

        await using var ctx = NewContext();
        var stored = await ctx.QueuedMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);
        Assert.AreEqual(MessageStatus.Delivered, stored.Status);
        Assert.AreEqual(0, stored.RawMessage.Length, "a fully-delivered message has nothing to resend, so its body is stripped");
        Assert.AreEqual("ok@example.com", stored.DeliveredRecipients);
    }

    /// <summary>
    /// Controllable <see cref="IDeliveryService"/>: records the recipients it was asked to deliver to and,
    /// optionally, throws a configured exception to simulate a delivery failure.
    /// </summary>
    private sealed class StubDeliveryService : IDeliveryService
    {
        public List<string> DeliveredRecipients { get; } = [];
        public int CallCount { get; private set; }
        public Func<QueuedMessage, Exception>? FailWith { get; set; }

        public Task<IReadOnlyList<DeliveryResult>> DeliverAsync(QueuedMessage message, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (FailWith is not null)
                throw FailWith(message);

            var recipients = message.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            DeliveredRecipients.AddRange(recipients);
            IReadOnlyList<DeliveryResult> results = recipients
                .Select(r => new DeliveryResult
                {
                    Recipient = r,
                    StatusCode = "250",
                    StatusMessage = "OK",
                    RemoteServer = "mx.example.com"
                })
                .ToList();
            return Task.FromResult(results);
        }
    }
}

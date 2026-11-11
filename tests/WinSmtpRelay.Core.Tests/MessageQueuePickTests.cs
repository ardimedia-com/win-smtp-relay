using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// H6/H2: the queue pick is a SQL range filter that never loads a body it does not deliver, lists are body-free, a
/// claim is exclusive, and the outcome of an attempt is written in one statement.
/// </summary>
[TestClass]
public class MessageQueuePickTests
{
    private RelayDbContext _db = null!;
    private MessageQueue _queue = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>().UseSqlite("Data Source=:memory:").Options;
        _db = new RelayDbContext(options, new CurrentTenant());
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _queue = new MessageQueue(_db, new CurrentActor(), new AdminAuditService(_db));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    private async Task<long> AddAsync(int tenant = TenantDefaults.DefaultTenantId, DateTimeOffset? due = null,
        MessageStatus status = MessageStatus.Queued, string recipients = "r@example.com")
    {
        if (tenant != TenantDefaults.DefaultTenantId && !await _db.Tenants.AnyAsync(t => t.Id == tenant))
        {
            _db.Tenants.Add(new Tenant { Id = tenant, Name = $"t{tenant}", Slug = $"t{tenant}" });
            await _db.SaveChangesAsync();
        }
        var id = await _queue.EnqueueAsync(new QueuedMessage
        {
            TenantId = tenant,
            MessageId = $"<{Guid.NewGuid():N}@test>",
            Sender = "s@example.com",
            Recipients = recipients,
            RawMessage = "Subject: x\r\n\r\nbody"u8.ToArray(),
            Status = status,
            NextRetryUtc = due ?? DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        _db.ChangeTracker.Clear();
        return id;
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetPendingAsync_FiltersDueInSql_OrdersByDueTime_AndOmitsBodies()
    {
        var later = await AddAsync(due: DateTimeOffset.UtcNow.AddMinutes(-1));
        var earlier = await AddAsync(due: DateTimeOffset.UtcNow.AddMinutes(-10));
        await AddAsync(due: DateTimeOffset.UtcNow.AddHours(1));
        await AddAsync(status: MessageStatus.Delivered);

        var pending = await _queue.GetPendingAsync(10);

        CollectionAssert.AreEqual(new[] { earlier, later }, pending.Select(m => m.Id).ToArray());
        Assert.IsTrue(pending.All(m => m.RawMessage.Length == 0), "listings never load bodies");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Lists_AndSummaries_OmitBodies()
    {
        await AddAsync();
        await AddAsync(status: MessageStatus.Delivered);

        Assert.AreEqual(2, (await _queue.GetRecentAsync(10)).Count(m => m.RawMessage.Length == 0));
        Assert.AreEqual(1, (await _queue.GetNonDeliveredAsync(10)).Count);
        Assert.AreEqual(2, (await _queue.GetSummariesAsync(10, nonDeliveredOnly: false)).Count);
        Assert.AreEqual(MessageStatus.Queued, (await _queue.GetSummariesAsync(10, nonDeliveredOnly: true)).Single().Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task DueTenantsAndCandidates_PerTenant()
    {
        await AddAsync(tenant: 1);
        var t2 = await AddAsync(tenant: 2, recipients: "a@x.test;b@y.test");
        await AddAsync(tenant: 3, due: DateTimeOffset.UtcNow.AddHours(1));

        var now = DateTimeOffset.UtcNow;
        CollectionAssert.AreEqual(new[] { 1, 2 }, (await _queue.GetDueTenantsAsync(now)).ToArray());
        var candidate = (await _queue.GetDueCandidatesAsync(2, now, 10)).Single();
        Assert.AreEqual(t2, candidate.Id);
        Assert.AreEqual("a@x.test;b@y.test", candidate.Recipients);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task TryClaim_IsExclusive_AndReturnsTheBody()
    {
        var id = await AddAsync();
        var lease = DateTimeOffset.UtcNow.AddMinutes(15);

        var first = await _queue.TryClaimAsync(id, lease);
        var second = await _queue.TryClaimAsync(id, lease);

        Assert.IsNotNull(first);
        Assert.IsTrue(first.RawMessage.Length > 0);
        Assert.AreEqual(MessageStatus.Delivering, first.Status);
        Assert.IsNotNull(first.LeaseExpiresUtc);
        Assert.IsNull(second, "a message is claimed once");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CompleteAttempt_WritesEverythingAndReleasesTheLease()
    {
        var id = await AddAsync();
        await _queue.TryClaimAsync(id, DateTimeOffset.UtcNow.AddMinutes(15));
        var next = DateTimeOffset.UtcNow.AddMinutes(5);

        await _queue.CompleteAttemptAsync(id, new DeliveryAttemptOutcome(MessageStatus.Queued, "a@x.test", "b@x.test", 1, next, "4.4.1"));

        _db.ChangeTracker.Clear();
        var m = (await _queue.GetByIdAsync(id))!;
        Assert.AreEqual(MessageStatus.Queued, m.Status);
        Assert.AreEqual("a@x.test", m.DeliveredRecipients);
        Assert.AreEqual("b@x.test", m.FailedRecipients);
        Assert.AreEqual(1, m.RetryCount);
        Assert.IsNull(m.LeaseExpiresUtc);
        Assert.IsNull(m.CompletedUtc);

        await _queue.CompleteAttemptAsync(id, new DeliveryAttemptOutcome(MessageStatus.Bounced, "a@x.test", "b@x.test", 2, next, "x"));
        _db.ChangeTracker.Clear();
        Assert.IsNotNull((await _queue.GetByIdAsync(id))!.CompletedUtc);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RequeueStaleDelivering_MakesTheMessageDueNow()
    {
        var id = await AddAsync(status: MessageStatus.Delivering, due: DateTimeOffset.UtcNow.AddHours(5));

        Assert.AreEqual(1, await _queue.RequeueStaleDeliveringAsync());

        Assert.AreEqual(id, (await _queue.GetPendingAsync(10)).Single().Id);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Requeue_ClearsPermanentFailures_KeepsDelivered()
    {
        var id = await AddAsync();
        await _queue.CompleteAttemptAsync(id, new DeliveryAttemptOutcome(MessageStatus.Bounced, "a@x.test", "b@x.test", 3, DateTimeOffset.UtcNow, "x"));

        Assert.IsTrue(await _queue.RequeueAsync(id));

        _db.ChangeTracker.Clear();
        var m = (await _queue.GetByIdAsync(id))!;
        Assert.AreEqual("a@x.test", m.DeliveredRecipients);
        Assert.IsNull(m.FailedRecipients);
        Assert.AreEqual(0, m.RetryCount);
    }
}

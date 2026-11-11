using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// Tests <see cref="MessageResendService"/> — cloning a stored message's body into a fresh queue entry
/// addressed to a chosen recipient set, while leaving the original untouched and refusing to resend a
/// message whose body has already been stripped.
/// </summary>
[TestClass]
public class MessageResendServiceTests
{
    private RelayDbContext _db = null!;
    private MessageResendService _svc = null!;
    private FakeBodyAccess _bodies = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new RelayDbContext(options, new CurrentTenant());
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _bodies = new FakeBodyAccess(_db);
        _svc = new MessageResendService(_db, _bodies);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Resend_ClonesBodyToNewQueuedMessage_LeavesOriginalUntouched()
    {
        var body = "Subject: Hi\r\n\r\nBody"u8.ToArray();
        var id = await AddAsync(body, recipients: "a@x.com;b@x.com", delivered: "a@x.com");

        var outcome = await _svc.ResendAsync(id, ["b@x.com"]);

        Assert.IsTrue(outcome.Success, outcome.Error);
        _db.ChangeTracker.Clear();
        var fresh = await _db.QueuedMessages.FirstAsync(m => m.Id == outcome.NewMessageId);
        Assert.AreEqual(MessageStatus.Queued, fresh.Status);
        Assert.AreEqual("b@x.com", fresh.Recipients);
        Assert.IsNull(fresh.DeliveredRecipients);
        Assert.AreEqual(0, fresh.RetryCount);
        CollectionAssert.AreEqual(body, fresh.RawMessage);

        var original = await _db.QueuedMessages.FirstAsync(m => m.Id == id);
        Assert.AreEqual(MessageStatus.Delivered, original.Status, "the original message must be left untouched");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Resend_DeduplicatesAndTrimsRecipients()
    {
        var id = await AddAsync("x"u8.ToArray());

        var outcome = await _svc.ResendAsync(id, [" b@x.com ", "b@x.com", "c@x.com"]);

        Assert.IsTrue(outcome.Success, outcome.Error);
        _db.ChangeTracker.Clear();
        var fresh = await _db.QueuedMessages.FirstAsync(m => m.Id == outcome.NewMessageId);
        Assert.AreEqual("b@x.com;c@x.com", fresh.Recipients);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Resend_Fails_WhenBodyAlreadyStripped()
    {
        var id = await AddAsync([]); // stripped body

        var outcome = await _svc.ResendAsync(id, ["b@x.com"]);

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Error, "no longer retained");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Resend_Fails_WhenNoRecipients()
    {
        var id = await AddAsync("x"u8.ToArray());

        var outcome = await _svc.ResendAsync(id, []);

        Assert.IsFalse(outcome.Success);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Resend_Fails_OnInvalidAddress()
    {
        var id = await AddAsync("x"u8.ToArray());

        var outcome = await _svc.ResendAsync(id, ["not-an-email"]);

        Assert.IsFalse(outcome.Success);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Resend_Fails_WhenMessageMissing()
    {
        var outcome = await _svc.ResendAsync(999999, ["b@x.com"]);

        Assert.IsFalse(outcome.Success);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Resend_ReadsTheBodyThroughTheGate_WithPurposeResend()
    {
        var id = await AddAsync("x"u8.ToArray());

        var outcome = await _svc.ResendAsync(id, ["b@x.com"]);

        Assert.IsTrue(outcome.Success, outcome.Error);
        CollectionAssert.AreEqual(new[] { (id, "resend") }, _bodies.Reads);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Resend_Stops_WhenTheGateRefuses()
    {
        var id = await AddAsync("x"u8.ToArray());
        _bodies.Force = MessageBodyReadStatus.Forbidden;

        var outcome = await _svc.ResendAsync(id, ["b@x.com"]);

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Error, "tenant-admin membership");
        Assert.AreEqual(1, await _db.QueuedMessages.CountAsync(), "nothing may be queued without consent");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Resend_InvalidRecipients_DoNotReadTheBody()
    {
        var id = await AddAsync("x"u8.ToArray());

        await _svc.ResendAsync(id, ["not-an-email"]);

        Assert.AreEqual(0, _bodies.Reads.Count);
    }

    /// <summary>Stands in for <see cref="MessageBodyAccess"/>: returns the stored body unless a status is forced.</summary>
    private sealed class FakeBodyAccess(RelayDbContext db) : IMessageBodyAccess
    {
        public List<(long, string)> Reads { get; } = [];
        public MessageBodyReadStatus? Force { get; set; }

        public async Task<MessageBodyReadResult> ReadAsync(long messageId, string purpose, CancellationToken ct = default)
        {
            Reads.Add((messageId, purpose));
            if (Force is { } forced)
                return new MessageBodyReadResult(forced);
            var body = await db.QueuedMessages.AsNoTracking().Where(m => m.Id == messageId).Select(m => m.RawMessage).FirstOrDefaultAsync(ct);
            if (body is null)
                return new MessageBodyReadResult(MessageBodyReadStatus.NotFound);
            return body.Length == 0
                ? new MessageBodyReadResult(MessageBodyReadStatus.NoBody)
                : new MessageBodyReadResult(MessageBodyReadStatus.Ok, body);
        }
    }

    private async Task<long> AddAsync(byte[] body, string recipients = "a@x.com;b@x.com", string? delivered = "a@x.com")
    {
        var m = new QueuedMessage
        {
            MessageId = "<m@test>",
            Sender = "sender@x.com",
            Recipients = recipients,
            DeliveredRecipients = delivered,
            RawMessage = body,
            SizeBytes = body.Length,
            Status = MessageStatus.Delivered
        };
        _db.QueuedMessages.Add(m);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return m.Id;
    }
}

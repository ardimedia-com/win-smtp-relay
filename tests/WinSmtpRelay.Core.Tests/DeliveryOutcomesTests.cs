using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// The daily report counts per recipient (T245): a first attempt that failed (smart host unreachable, timeout,
/// TLS) and a retry that delivered is "delivered + retried", never a bounce (WUHARRYHOME 17.–19.10.2026:
/// «Bounced: 2 (50 %)» although every message was delivered).
/// </summary>
[TestClass]
public class DeliveryOutcomesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);

    private static DeliveryAttempt A(long msg, string rcpt, string code, int minutes, string? text = null) =>
        new(msg, rcpt, code, text ?? code, T0.AddMinutes(minutes));

    [TestMethod]
    [TestCategory("Unit")]
    public void FailedFirstAttempt_DeliveredOnRetry_IsNotABounce()
    {
        // The WUHARRYHOME pattern: 2 messages, each failed once ("500 A task was canceled") and delivered 5 min later.
        var attempts = new[]
        {
            A(1, "a@example.com", "500", 0, "A task was canceled."),
            A(1, "a@example.com", "250", 5),
            A(2, "b@example.com", "500", 10, "Der angegebene Host ist unbekannt"),
            A(2, "b@example.com", "250", 15),
        };
        var statuses = new Dictionary<long, MessageStatus> { [1] = MessageStatus.Delivered, [2] = MessageStatus.Delivered };

        var c = DeliveryOutcomes.Classify(attempts, statuses);

        Assert.AreEqual(2, c.Delivered);
        Assert.AreEqual(0, c.Bounced);
        Assert.AreEqual(0, c.Deferred);
        Assert.AreEqual(2, c.RetriedAttempts);
        Assert.AreEqual(0, c.BounceRatePercent);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void FinalFailure_CountsOncePerRecipient()
    {
        // Three failed attempts, then retries exhausted → one bounced recipient, not three.
        var attempts = new[]
        {
            A(1, "a@example.com", "500", 0), A(1, "a@example.com", "500", 30), A(1, "a@example.com", "550", 60),
            A(2, "b@example.com", "250", 0),
        };
        var statuses = new Dictionary<long, MessageStatus> { [1] = MessageStatus.Bounced };

        var c = DeliveryOutcomes.Classify(attempts, statuses);

        Assert.AreEqual(1, c.Delivered);
        Assert.AreEqual(1, c.Bounced);
        Assert.AreEqual(0, c.RetriedAttempts);
        Assert.AreEqual(50, c.BounceRatePercent);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void StillQueued_IsDeferred_NotBounced()
    {
        var attempts = new[] { A(1, "a@example.com", "500", 0), A(1, "a@example.com", "421", 5) };
        var c = DeliveryOutcomes.Classify(attempts, new Dictionary<long, MessageStatus> { [1] = MessageStatus.Queued });

        Assert.AreEqual(0, c.Bounced);
        Assert.AreEqual(1, c.Deferred);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PartialSuccess_OnlyTheFailedRecipientBounces()
    {
        var attempts = new[]
        {
            A(1, "good@example.com", "250", 0),
            A(1, "bad@example.org", "550", 0, "5.1.1 user unknown"),
        };
        var c = DeliveryOutcomes.Classify(attempts, new Dictionary<long, MessageStatus> { [1] = MessageStatus.Bounced });

        Assert.AreEqual(1, c.Delivered);
        Assert.AreEqual(1, c.Bounced);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Suppressed_IsCountedSeparately()
    {
        var attempts = new[] { A(1, "x@example.com", "550", 0, "Suppressed: address previously hard-bounced or complained") };
        var c = DeliveryOutcomes.Classify(attempts, new Dictionary<long, MessageStatus> { [1] = MessageStatus.Suppressed });

        Assert.AreEqual(1, c.Suppressed);
        Assert.AreEqual(0, c.Bounced);
        Assert.AreEqual(0, c.Recipients);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PurgedMessage_JudgedByTheLastAttempt()
    {
        var attempts = new[] { A(1, "a@example.com", "550", 0), A(2, "b@example.com", "451", 0) };
        var c = DeliveryOutcomes.Classify(attempts, new Dictionary<long, MessageStatus>());

        Assert.AreEqual(1, c.Bounced);
        Assert.AreEqual(1, c.Deferred);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Counter_ReadsTheDeliveryLog_AndMessageStatus()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var db = new RelayDbContext(options, new CurrentTenant());
        db.Database.OpenConnection();
        db.Database.EnsureCreated();

        var delivered = new QueuedMessage { MessageId = "m1", Sender = "s@example.com", Recipients = "a@example.com", RawMessage = [], Status = MessageStatus.Delivered };
        var bounced = new QueuedMessage { MessageId = "m2", Sender = "s@example.com", Recipients = "b@example.com", RawMessage = [], Status = MessageStatus.Bounced };
        db.QueuedMessages.AddRange(delivered, bounced);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        db.DeliveryLogs.AddRange(
            new DeliveryLog { QueuedMessageId = delivered.Id, Recipient = "a@example.com", StatusCode = "500", StatusMessage = "A task was canceled.", TimestampUtc = now.AddMinutes(-10) },
            new DeliveryLog { QueuedMessageId = delivered.Id, Recipient = "a@example.com", StatusCode = "250", StatusMessage = "OK", TimestampUtc = now.AddMinutes(-5) },
            new DeliveryLog { QueuedMessageId = bounced.Id, Recipient = "b@example.com", StatusCode = "550", StatusMessage = "user unknown", TimestampUtc = now.AddMinutes(-3) },
            new DeliveryLog { QueuedMessageId = bounced.Id, Recipient = "b@example.com", StatusCode = "550", StatusMessage = "old", TimestampUtc = now.AddDays(-3) });
        await db.SaveChangesAsync();

        var c = await DeliveryOutcomeCounter.CountAsync(db, now.AddHours(-24), CancellationToken.None);

        Assert.AreEqual(1, c.Delivered);
        Assert.AreEqual(1, c.Bounced);
        Assert.AreEqual(1, c.RetriedAttempts);
    }
}

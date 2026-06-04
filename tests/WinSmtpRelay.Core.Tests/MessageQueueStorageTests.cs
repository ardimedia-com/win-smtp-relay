using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class MessageQueueStorageTests
{
    private RelayDbContext _db = null!;
    private MessageQueue _queue = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new RelayDbContext(options, new CurrentTenant());
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _queue = new MessageQueue(_db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EnqueueAsync_ReturnsId()
    {
        var message = CreateMessage();
        var id = await _queue.EnqueueAsync(message);
        Assert.IsTrue(id > 0);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetPendingAsync_ReturnsQueuedMessages()
    {
        var msg = CreateMessage();
        msg.NextRetryUtc = DateTime.UtcNow.AddMinutes(-1);
        await _queue.EnqueueAsync(msg);

        var pending = await _queue.GetPendingAsync(10);
        Assert.AreEqual(1, pending.Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetPendingAsync_SkipsFutureRetries()
    {
        var msg = CreateMessage();
        msg.NextRetryUtc = DateTime.UtcNow.AddHours(1);
        await _queue.EnqueueAsync(msg);

        var pending = await _queue.GetPendingAsync(10);
        Assert.AreEqual(0, pending.Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task UpdateStatusAsync_SetsDelivered()
    {
        var msg = CreateMessage();
        var id = await _queue.EnqueueAsync(msg);

        await _queue.UpdateStatusAsync(id, MessageStatus.Delivered);

        // ExecuteUpdateAsync bypasses change tracker — reload the entity
        var entry = _db.ChangeTracker.Entries<QueuedMessage>().First(e => e.Entity.Id == id);
        await entry.ReloadAsync();
        var updated = entry.Entity;

        Assert.AreEqual(MessageStatus.Delivered, updated.Status);
        Assert.IsNotNull(updated.CompletedUtc);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SetRetryAsync_UpdatesRetryCountAndNextRetry()
    {
        var msg = CreateMessage();
        var id = await _queue.EnqueueAsync(msg);

        var nextRetry = DateTime.UtcNow.AddMinutes(5);
        await _queue.SetRetryAsync(id, 2, nextRetry);

        // Need fresh context to see ExecuteUpdate changes
        var entry = _db.ChangeTracker.Entries<QueuedMessage>().First();
        await entry.ReloadAsync();
        var updated = entry.Entity;

        Assert.AreEqual(2, updated.RetryCount);
        Assert.IsNotNull(updated.NextRetryUtc);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetQueueDepthAsync_CountsOnlyQueued()
    {
        var msg1 = CreateMessage();
        var msg2 = CreateMessage();
        await _queue.EnqueueAsync(msg1);
        var id2 = await _queue.EnqueueAsync(msg2);
        await _queue.UpdateStatusAsync(id2, MessageStatus.Delivered);

        var depth = await _queue.GetQueueDepthAsync();
        Assert.AreEqual(1, depth);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task DeleteAsync_RemovesMessage()
    {
        var msg = CreateMessage();
        var id = await _queue.EnqueueAsync(msg);

        await _queue.DeleteAsync(id);

        // ExecuteDeleteAsync bypasses change tracker — detach and re-query
        _db.ChangeTracker.Clear();
        var deleted = await _queue.GetByIdAsync(id);
        Assert.IsNull(deleted);
    }

    private static QueuedMessage CreateMessage() => new()
    {
        MessageId = $"<{Guid.NewGuid()}@test>",
        Sender = "sender@example.com",
        Recipients = "recipient@example.com",
        RawMessage = "Subject: Test\r\n\r\nBody"u8.ToArray(),
        SizeBytes = 100,
        NextRetryUtc = DateTime.UtcNow
    };
}

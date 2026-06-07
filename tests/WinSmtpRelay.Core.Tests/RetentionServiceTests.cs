using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class RetentionServiceTests
{
    private RelayDbContext _db = null!;
    private DataRetentionSettingsService _settings = null!;
    private RetentionService _retention = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new RelayDbContext(options, new CurrentTenant());
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _settings = new DataRetentionSettingsService(_db);
        _retention = new RetentionService(_db, _settings, NullLogger<RetentionService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunPurge_RemovesOldTerminalMessages_KeepsRecentAndInFlight()
    {
        var now = DateTimeOffset.UtcNow;
        await AddMessageAsync(MessageStatus.Delivered, now.AddDays(-40)); // old terminal -> purge
        await AddMessageAsync(MessageStatus.Bounced, now.AddDays(-40));   // old terminal -> purge
        await AddMessageAsync(MessageStatus.Delivered, now.AddDays(-5));  // recent terminal -> keep
        await AddMessageAsync(MessageStatus.Queued, now.AddDays(-40));    // in-flight -> keep regardless of age

        await SaveSettings(messageHistoryDays: 30, deliveryLogDays: 90, suppressionDays: 0);

        var result = await _retention.RunPurgeAsync();

        Assert.AreEqual(2, result.Messages);
        _db.ChangeTracker.Clear();
        Assert.AreEqual(2, await _db.QueuedMessages.CountAsync());
        Assert.IsFalse(await _db.QueuedMessages.AnyAsync(m => m.Status == MessageStatus.Bounced));
        Assert.IsTrue(await _db.QueuedMessages.AnyAsync(m => m.Status == MessageStatus.Queued));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunPurge_RemovesOldDeliveryLogs_KeepsRecent()
    {
        var now = DateTimeOffset.UtcNow;
        await AddLogAsync(now.AddDays(-100));
        await AddLogAsync(now.AddDays(-10));

        await SaveSettings(messageHistoryDays: 30, deliveryLogDays: 90, suppressionDays: 0);

        var result = await _retention.RunPurgeAsync();

        Assert.AreEqual(1, result.DeliveryLogs);
        _db.ChangeTracker.Clear();
        Assert.AreEqual(1, await _db.DeliveryLogs.CountAsync());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunPurge_KeepsSuppressionsWhenZero_PurgesWhenWindowSet()
    {
        var now = DateTimeOffset.UtcNow;
        await AddSuppressionAsync("old@example.com", now.AddDays(-400));
        await AddSuppressionAsync("recent@example.com", now.AddDays(-10));

        // 0 = keep forever
        await SaveSettings(messageHistoryDays: 30, deliveryLogDays: 90, suppressionDays: 0);
        var keepAll = await _retention.RunPurgeAsync();
        Assert.AreEqual(0, keepAll.Suppressions);
        _db.ChangeTracker.Clear();
        Assert.AreEqual(2, await _db.SuppressionEntries.CountAsync());

        // 365-day window purges the 400-day-old entry only
        await SaveSettings(messageHistoryDays: 30, deliveryLogDays: 90, suppressionDays: 365);
        var purged = await _retention.RunPurgeAsync();
        Assert.AreEqual(1, purged.Suppressions);
        _db.ChangeTracker.Clear();
        Assert.AreEqual(1, await _db.SuppressionEntries.CountAsync());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task UpdateSettings_FloorsDeliveryLogRetention()
    {
        await _settings.UpdateAsync(new DataRetentionSettings
        {
            Profile = "Custom",
            StripBodyOnDelivery = true,
            MessageHistoryDays = 30,
            DeliveryLogDays = 1, // below the floor
            SuppressionDays = 0
        });

        var stored = await _settings.GetAsync();
        Assert.AreEqual(DataRetentionSettings.DeliveryLogFloorDays, stored.DeliveryLogDays);
    }

    private async Task SaveSettings(int messageHistoryDays, int deliveryLogDays, int suppressionDays)
    {
        await _settings.UpdateAsync(new DataRetentionSettings
        {
            Profile = "Custom",
            StripBodyOnDelivery = true,
            MessageHistoryDays = messageHistoryDays,
            DeliveryLogDays = deliveryLogDays,
            SuppressionDays = suppressionDays
        });
        _db.ChangeTracker.Clear();
    }

    private async Task AddMessageAsync(MessageStatus status, DateTimeOffset createdUtc)
    {
        _db.QueuedMessages.Add(new QueuedMessage
        {
            MessageId = $"<{Guid.NewGuid()}@test>",
            Sender = "sender@example.com",
            Recipients = "recipient@example.com",
            RawMessage = "Subject: Test\r\n\r\nBody"u8.ToArray(),
            SizeBytes = 100,
            Status = status,
            CreatedUtc = createdUtc
        });
        await _db.SaveChangesAsync();
    }

    private async Task AddLogAsync(DateTimeOffset timestampUtc)
    {
        _db.DeliveryLogs.Add(new DeliveryLog
        {
            QueuedMessageId = 0,
            Recipient = "recipient@example.com",
            StatusCode = "250",
            StatusMessage = "OK",
            TimestampUtc = timestampUtc
        });
        await _db.SaveChangesAsync();
    }

    private async Task AddSuppressionAsync(string address, DateTimeOffset createdUtc)
    {
        _db.SuppressionEntries.Add(new SuppressionEntry
        {
            Address = address,
            Reason = SuppressionReason.HardBounce,
            CreatedUtc = createdUtc
        });
        await _db.SaveChangesAsync();
    }
}

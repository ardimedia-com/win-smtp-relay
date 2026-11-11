using Microsoft.Extensions.Logging.Abstractions;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Security.Tests;

[TestClass]
public class RateLimiterTests
{
    private static RateLimiter CreateLimiter(RateLimitSettings? settings = null)
        => new(new StubCache(settings ?? new RateLimitSettings()), NullLogger<RateLimiter>.Instance);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static (RateLimiter Limiter, ManualClock Clock) CreateWithClock(RateLimitSettings? settings = null)
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
        return (new RateLimiter(new StubCache(settings ?? new RateLimitSettings()), NullLogger<RateLimiter>.Instance, clock), clock);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void UserBudget_NoLimits_AlwaysWithinLimit()
    {
        var limiter = CreateLimiter();
        limiter.RecordUserRecipients("user1", null, null, 50);
        Assert.IsTrue(limiter.IsUserWithinLimit("user1", null, null));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void UserBudget_CheckDoesNotConsume()
    {
        // The MAIL-time check only asks; repeated MAILs without recipients never exhaust the budget.
        var limiter = CreateLimiter();
        for (var i = 0; i < 10; i++)
            Assert.IsTrue(limiter.IsUserWithinLimit("user1", 3, null));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void UserBudget_PerMinute_CountsRecipients()
    {
        var limiter = CreateLimiter();

        limiter.RecordUserRecipients("user1", 3, null, 2);
        Assert.IsTrue(limiter.IsUserWithinLimit("user1", 3, null));
        limiter.RecordUserRecipients("user1", 3, null);
        Assert.IsFalse(limiter.IsUserWithinLimit("user1", 3, null));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void UserBudget_DifferentUsers_IndependentLimits()
    {
        var limiter = CreateLimiter();

        limiter.RecordUserRecipients("user1", 1, null);
        Assert.IsFalse(limiter.IsUserWithinLimit("user1", 1, null));
        Assert.IsTrue(limiter.IsUserWithinLimit("user2", 1, null));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void UserBudget_PerDay_FreesUpAfterTheWindow()
    {
        var (limiter, clock) = CreateWithClock();

        limiter.RecordUserRecipients("user1", null, 2, 2);
        Assert.IsFalse(limiter.IsUserWithinLimit("user1", null, 2));

        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        Assert.IsTrue(limiter.IsUserWithinLimit("user1", null, 2));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task IsIpAllowed_BlocksAfterExceeded()
    {
        var limiter = CreateLimiter(new RateLimitSettings { MaxConnectionsPerIpPerMinute = 3 });

        Assert.IsTrue(await limiter.IsIpAllowedAsync("192.168.1.1"));
        Assert.IsTrue(await limiter.IsIpAllowedAsync("192.168.1.1"));
        Assert.IsTrue(await limiter.IsIpAllowedAsync("192.168.1.1"));
        Assert.IsFalse(await limiter.IsIpAllowedAsync("192.168.1.1"));

        // Different IP should still be allowed
        Assert.IsTrue(await limiter.IsIpAllowedAsync("192.168.1.2"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SenderBudget_CountsRecipientsNotMailCommands()
    {
        var limiter = CreateLimiter(new RateLimitSettings
        {
            MaxMessagesPerSenderPerMinute = 5,
            MaxMessagesPerSenderPerDay = 1000
        });

        // One message to four recipients leaves room for one more recipient this minute,
        await limiter.RecordSenderRecipientsAsync("user@example.com", 4);
        Assert.IsTrue(await limiter.IsSenderWithinLimitAsync("user@example.com"));

        // and the fifth exhausts it (the address is compared case-insensitively).
        await limiter.RecordSenderRecipientsAsync("User@Example.com", 1);
        Assert.IsFalse(await limiter.IsSenderWithinLimitAsync("user@example.com"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FailedAuth_BansIpAfterThreshold()
    {
        var limiter = CreateLimiter(new RateLimitSettings
        {
            FailedAuthBanThreshold = 3,
            FailedAuthBanMinutes = 30
        });

        var ip = "10.0.0.1";

        Assert.IsFalse(limiter.IsIpBanned(ip));

        await limiter.RecordFailedAuthAsync(ip);
        await limiter.RecordFailedAuthAsync(ip);
        Assert.IsFalse(limiter.IsIpBanned(ip));

        await limiter.RecordFailedAuthAsync(ip); // 3rd = threshold
        Assert.IsTrue(limiter.IsIpBanned(ip));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FailedAuth_FailuresDecayOutOfTheWindow()
    {
        // A success no longer clears the counter (M7); failures age out of the ban window instead.
        var (limiter, clock) = CreateWithClock(new RateLimitSettings { FailedAuthBanThreshold = 3, FailedAuthBanMinutes = 30 });
        var ip = "10.0.0.1";

        await limiter.RecordFailedAuthAsync(ip);
        await limiter.RecordFailedAuthAsync(ip);
        clock.Advance(TimeSpan.FromMinutes(31));
        await limiter.RecordFailedAuthAsync(ip);

        Assert.IsFalse(limiter.IsIpBanned(ip), "the two old failures have aged out");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FailedAuth_BanExpires()
    {
        var (limiter, clock) = CreateWithClock(new RateLimitSettings { FailedAuthBanThreshold = 1, FailedAuthBanMinutes = 15 });

        await limiter.RecordFailedAuthAsync("10.0.0.1");
        Assert.IsTrue(limiter.IsIpBanned("10.0.0.1"));

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.IsFalse(limiter.IsIpBanned("10.0.0.1"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FailedAuth_DistributedGuessing_LocksTheUsername()
    {
        // Every guess from a different address: no IP reaches its threshold, the username does.
        var limiter = CreateLimiter(new RateLimitSettings { FailedAuthBanThreshold = 2, FailedAuthBanMinutes = 30 });
        var lockAfter = 2 * RateLimiter.UsernameFailureMultiplier;

        for (var i = 0; i < lockAfter - 1; i++)
            await limiter.RecordFailedAuthAsync($"203.0.113.{i + 1}", "alice");
        Assert.IsFalse(limiter.IsUsernameLocked("alice"));

        await limiter.RecordFailedAuthAsync("203.0.113.200", "ALICE");
        Assert.IsTrue(limiter.IsUsernameLocked("alice"));
        Assert.IsFalse(limiter.IsUsernameLocked("bob"));
        Assert.IsFalse(limiter.IsIpBanned("203.0.113.1"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Sweep_EvictsEntriesWhoseWindowHasPassed()
    {
        var (limiter, clock) = CreateWithClock(new RateLimitSettings
        {
            MaxConnectionsPerIpPerMinute = 10,
            MaxMessagesPerSenderPerMinute = 10,
            FailedAuthBanThreshold = 5,
            FailedAuthBanMinutes = 30
        });

        for (var i = 0; i < 50; i++)
        {
            await limiter.IsIpAllowedAsync($"198.51.100.{i}");
            await limiter.RecordSenderRecipientsAsync($"s{i}@example.com");
            await limiter.RecordFailedAuthAsync($"198.51.100.{i}", $"user{i}");
        }
        Assert.AreEqual(200, limiter.TrackedEntryCount);

        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        limiter.Sweep(clock.GetUtcNow());

        Assert.AreEqual(0, limiter.TrackedEntryCount);
    }

    /// <summary>Minimal cache stub: the rate limiter only reads <see cref="GetRateLimitSettingsAsync"/>.</summary>
    private sealed class StubCache(RateLimitSettings settings) : IRuntimeConfigCache
    {
        public Task<RateLimitSettings> GetRateLimitSettingsAsync(CancellationToken ct = default)
            => Task.FromResult(settings);

        public Task<IReadOnlyList<string>> GetAcceptedDomainsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetAcceptedSenderDomainsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlySet<string>> GetVerifiedSenderDomainsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlySet<string>> GetVerifiedRecipientDomainsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<IpAccessRule>> GetIpAccessRulesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> GetTenantForSenderDomainAsync(string domain, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> GetTenantForRecipientDomainAsync(string domain, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> IsTenantEnabledAsync(int tenantId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetTenantEgressIpAsync(int tenantId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EmailAuthSettings> GetEmailAuthSettingsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BackupMxSettings> GetBackupMxSettingsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DomainRoute>> GetDomainRoutesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SendConnector?> GetDefaultConnectorAsync(int tenantId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<HeaderRewriteEntry>> GetHeaderRewriteRulesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SenderRewriteEntry>> GetSenderRewriteRulesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public void Invalidate() { }
    }
}

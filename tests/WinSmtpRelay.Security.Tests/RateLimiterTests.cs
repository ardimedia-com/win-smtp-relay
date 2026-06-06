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

    [TestMethod]
    [TestCategory("Unit")]
    public void IsAllowed_NoLimits_AlwaysAllowed()
    {
        var limiter = CreateLimiter();
        Assert.IsTrue(limiter.IsAllowed("user1", null, null));
        Assert.IsTrue(limiter.IsAllowed("user1", null, null));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsAllowed_PerMinuteLimit_BlocksAfterExceeded()
    {
        var limiter = CreateLimiter();

        Assert.IsTrue(limiter.IsAllowed("user1", 3, null));
        Assert.IsTrue(limiter.IsAllowed("user1", 3, null));
        Assert.IsTrue(limiter.IsAllowed("user1", 3, null));
        Assert.IsFalse(limiter.IsAllowed("user1", 3, null));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsAllowed_DifferentUsers_IndependentLimits()
    {
        var limiter = CreateLimiter();

        Assert.IsTrue(limiter.IsAllowed("user1", 1, null));
        Assert.IsFalse(limiter.IsAllowed("user1", 1, null));

        Assert.IsTrue(limiter.IsAllowed("user2", 1, null));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsAllowed_PerDayLimit_BlocksAfterExceeded()
    {
        var limiter = CreateLimiter();

        Assert.IsTrue(limiter.IsAllowed("user1", null, 2));
        Assert.IsTrue(limiter.IsAllowed("user1", null, 2));
        Assert.IsFalse(limiter.IsAllowed("user1", null, 2));
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
    public async Task IsSenderAllowed_BlocksAfterExceeded()
    {
        var limiter = CreateLimiter(new RateLimitSettings
        {
            MaxMessagesPerSenderPerMinute = 2,
            MaxMessagesPerSenderPerDay = 1000
        });

        Assert.IsTrue(await limiter.IsSenderAllowedAsync("user@example.com"));
        Assert.IsTrue(await limiter.IsSenderAllowedAsync("user@example.com"));
        Assert.IsFalse(await limiter.IsSenderAllowedAsync("user@example.com"));
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
    public async Task ClearFailedAuth_RemovesBan()
    {
        var limiter = CreateLimiter(new RateLimitSettings
        {
            FailedAuthBanThreshold = 1,
            FailedAuthBanMinutes = 60
        });

        var ip = "10.0.0.1";
        await limiter.RecordFailedAuthAsync(ip);
        Assert.IsTrue(limiter.IsIpBanned(ip));

        limiter.ClearFailedAuth(ip);
        Assert.IsFalse(limiter.IsIpBanned(ip));
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

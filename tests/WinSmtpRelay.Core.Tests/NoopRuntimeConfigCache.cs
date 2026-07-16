using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// Satisfies the config-service constructors in tests that assert persistence, not caching. The storage
/// services invalidate the runtime cache themselves after every mutation (so no caller can forget to),
/// which makes the cache a required dependency even where a test never reads it.
/// </summary>
internal sealed class NoopRuntimeConfigCache : IRuntimeConfigCache
{
    public Task<IReadOnlyList<string>> GetAcceptedDomainsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> GetAcceptedSenderDomainsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlySet<string>> GetVerifiedSenderDomainsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    public Task<IReadOnlySet<string>> GetVerifiedRecipientDomainsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    public Task<IReadOnlyList<IpAccessRule>> GetIpAccessRulesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IpAccessRule>>([]);
    public Task<int?> GetTenantForSenderDomainAsync(string domain, CancellationToken ct = default)
        => Task.FromResult<int?>(null);
    public Task<int?> GetTenantForRecipientDomainAsync(string domain, CancellationToken ct = default)
        => Task.FromResult<int?>(null);
    public Task<bool> IsTenantEnabledAsync(int tenantId, CancellationToken ct = default)
        => Task.FromResult(true);
    public Task<string?> GetTenantEgressIpAsync(int tenantId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task<RateLimitSettings> GetRateLimitSettingsAsync(CancellationToken ct = default)
        => Task.FromResult(new RateLimitSettings());
    public Task<EmailAuthSettings> GetEmailAuthSettingsAsync(CancellationToken ct = default)
        => Task.FromResult(new EmailAuthSettings());
    public Task<BackupMxSettings> GetBackupMxSettingsAsync(CancellationToken ct = default)
        => Task.FromResult(new BackupMxSettings());
    public Task<IReadOnlyList<DomainRoute>> GetDomainRoutesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DomainRoute>>([]);
    public Task<SendConnector?> GetDefaultConnectorAsync(int tenantId, CancellationToken ct = default)
        => Task.FromResult<SendConnector?>(null);
    public Task<IReadOnlyList<HeaderRewriteEntry>> GetHeaderRewriteRulesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HeaderRewriteEntry>>([]);
    public Task<IReadOnlyList<SenderRewriteEntry>> GetSenderRewriteRulesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SenderRewriteEntry>>([]);
    public void Invalidate() { }
}

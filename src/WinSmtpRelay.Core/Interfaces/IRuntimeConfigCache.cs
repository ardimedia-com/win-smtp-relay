using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// In-memory cache for runtime-editable configuration stored in SQLite.
/// Loaded lazily on first access; invalidated when Admin API modifies data.
/// </summary>
public interface IRuntimeConfigCache
{
    Task<IReadOnlyList<string>> GetAcceptedDomainsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAcceptedSenderDomainsAsync(CancellationToken ct = default);

    /// <summary>IP access rules ordered by <see cref="IpAccessRule.SortOrder"/> (authoritative relay IP policy).</summary>
    Task<IReadOnlyList<IpAccessRule>> GetIpAccessRulesAsync(CancellationToken ct = default);

    /// <summary>The tenant that owns an accepted sender domain, or null if no tenant claims it.</summary>
    Task<int?> GetTenantForSenderDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>The tenant that owns an accepted recipient domain, or null if no tenant claims it.</summary>
    Task<int?> GetTenantForRecipientDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>True if the tenant exists and is enabled. Used to gate the SMTP path for disabled tenants.</summary>
    Task<bool> IsTenantEnabledAsync(int tenantId, CancellationToken ct = default);

    Task<IReadOnlyList<DomainRoute>> GetDomainRoutesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HeaderRewriteEntry>> GetHeaderRewriteRulesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SenderRewriteEntry>> GetSenderRewriteRulesAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears all cached data. Next access triggers a fresh DB load.
    /// </summary>
    void Invalidate();
}

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

    /// <summary>Accepted sender domains whose ownership is verified — used for the verification enforcement gate.</summary>
    Task<IReadOnlySet<string>> GetVerifiedSenderDomainsAsync(CancellationToken ct = default);

    /// <summary>Accepted recipient domains whose ownership is verified — used for the verification enforcement gate.</summary>
    Task<IReadOnlySet<string>> GetVerifiedRecipientDomainsAsync(CancellationToken ct = default);

    /// <summary>IP access rules ordered by <see cref="IpAccessRule.SortOrder"/> (authoritative relay IP policy).</summary>
    Task<IReadOnlyList<IpAccessRule>> GetIpAccessRulesAsync(CancellationToken ct = default);

    /// <summary>The tenant that owns an accepted sender domain, or null if no tenant claims it.</summary>
    Task<int?> GetTenantForSenderDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>The tenant that owns an accepted recipient domain, or null if no tenant claims it.</summary>
    Task<int?> GetTenantForRecipientDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>True if the tenant exists and is enabled. Used to gate the SMTP path for disabled tenants.</summary>
    Task<bool> IsTenantEnabledAsync(int tenantId, CancellationToken ct = default);

    /// <summary>The tenant's configured outbound source IP, or null to use the OS default.</summary>
    Task<string?> GetTenantEgressIpAsync(int tenantId, CancellationToken ct = default);

    /// <summary>The host-level rate-limit settings (single row), cached for the SMTP hot path.</summary>
    Task<RateLimitSettings> GetRateLimitSettingsAsync(CancellationToken ct = default);

    /// <summary>The host-level inbound email-authentication policy (single row), cached for the SMTP hot path.</summary>
    Task<EmailAuthSettings> GetEmailAuthSettingsAsync(CancellationToken ct = default);

    /// <summary>The host-level backup-MX settings (single row), cached for the SMTP/delivery hot path.</summary>
    Task<BackupMxSettings> GetBackupMxSettingsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DomainRoute>> GetDomainRoutesAsync(CancellationToken ct = default);

    /// <summary>The tenant's enabled "default" send connector, used as the routing fallback when no
    /// domain route matches (checked before the appsettings global smart host). Null if the tenant has
    /// no enabled default connector.</summary>
    Task<SendConnector?> GetDefaultConnectorAsync(int tenantId, CancellationToken ct = default);

    Task<IReadOnlyList<HeaderRewriteEntry>> GetHeaderRewriteRulesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SenderRewriteEntry>> GetSenderRewriteRulesAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears all cached data. Next access triggers a fresh DB load.
    /// </summary>
    void Invalidate();
}

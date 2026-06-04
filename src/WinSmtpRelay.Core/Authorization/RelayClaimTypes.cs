namespace WinSmtpRelay.Core.Authorization;

/// <summary>
/// Custom claim types emitted by both authentication schemes (cookie + API key)
/// so authorization and tenant-scoping work identically regardless of how the
/// caller authenticated.
/// </summary>
public static class RelayClaimTypes
{
    /// <summary>The tenant the principal belongs to. Absent for host-level principals.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>"true" when the principal is a host-level administrator (no single tenant).</summary>
    public const string IsHostAdmin = "is_host_admin";

    /// <summary>"true" when the user must change their password before normal use (e.g. seeded account).</summary>
    public const string MustChangePassword = "must_change_password";

    /// <summary>For a host admin: the tenant they are currently viewing/acting within (the switcher selection). Absent = all tenants.</summary>
    public const string ActiveTenant = "active_tenant";
}

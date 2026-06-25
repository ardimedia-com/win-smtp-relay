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

    /// <summary>"true" when the principal holds a host-level membership (host administrator).</summary>
    public const string IsHostAdmin = "is_host_admin";

    /// <summary>
    /// One claim per tenant membership the principal holds, value <c>"{tenantId}:{role}"</c>
    /// (e.g. <c>"5:TenantAdmin"</c>). The single source of truth for which tenants a principal may
    /// enter and with what role — there is no implicit cross-tenant access from a host membership.
    /// </summary>
    public const string TenantMembership = "tenant_membership";

    /// <summary>"true" when the user must change their password before normal use (e.g. seeded account).</summary>
    public const string MustChangePassword = "must_change_password";

    /// <summary>For a host admin: the tenant they are currently viewing/acting within (the switcher selection). Absent = all tenants.</summary>
    public const string ActiveTenant = "active_tenant";
}

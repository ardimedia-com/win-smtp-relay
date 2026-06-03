namespace WinSmtpRelay.Core.Authorization;

/// <summary>
/// Authorization policy names applied to admin endpoints and Blazor pages.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>Host-level administration (tenant CRUD, listener/cert/global settings). Requires <see cref="RelayRoles.HostAdmin"/>.</summary>
    public const string HostAdmin = "HostAdmin";

    /// <summary>Full read/write within a tenant. Satisfied by TenantAdmin or HostAdmin.</summary>
    public const string AdminFull = "AdminFull";

    /// <summary>Read access within a tenant. Satisfied by TenantViewer, TenantAdmin, or HostAdmin.</summary>
    public const string AdminView = "AdminView";
}

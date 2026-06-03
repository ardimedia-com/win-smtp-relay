namespace WinSmtpRelay.Core.Authorization;

/// <summary>
/// Web-admin role names. These are distinct from SMTP relay users
/// (<see cref="Models.RelayUser"/>); they govern access to the admin UI/API only.
/// </summary>
public static class RelayRoles
{
    /// <summary>Manages tenants and host-wide infrastructure (listeners, certificates, global settings). Not bound to a single tenant.</summary>
    public const string HostAdmin = "HostAdmin";

    /// <summary>Full read/write access to a single tenant's configuration.</summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>Read-only access to a single tenant's configuration and monitoring.</summary>
    public const string TenantViewer = "TenantViewer";

    public static readonly string[] All = [HostAdmin, TenantAdmin, TenantViewer];
}

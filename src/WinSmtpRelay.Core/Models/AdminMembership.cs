namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Grants a web-admin account (<c>AdminUser</c>) a role within a scope. A <b>host</b> membership
/// (<see cref="TenantId"/> == null) confers host-level administration (tenants, server config, host
/// admins); a <b>tenant</b> membership (<see cref="TenantId"/> set) confers a role within that one
/// tenant. A user may hold several memberships — a host membership and/or memberships in multiple
/// tenants. This is the single source of truth for "who may do what, where": there is no implicit
/// cross-tenant access. A host admin reaches a tenant only via an explicit tenant membership (granted
/// by a tenant admin, or self-granted as an audited <see cref="IsBreakGlass"/> override).
/// </summary>
public class AdminMembership
{
    public int Id { get; set; }

    /// <summary>The <c>AdminUser</c> this membership belongs to.</summary>
    public int UserId { get; set; }

    /// <summary>The tenant this membership grants access to; <c>null</c> = a host-level membership.</summary>
    public int? TenantId { get; set; }

    /// <summary>Role within the scope: <c>HostAdmin</c> (host membership) or <c>TenantAdmin</c>/<c>TenantViewer</c> (tenant).</summary>
    public string Role { get; set; } = "";

    /// <summary>The admin who created this membership (for audit); null for seeded/migrated rows.</summary>
    public int? GrantedByUserId { get; set; }

    /// <summary>True when a host admin self-granted this tenant membership as an audited emergency override.</summary>
    public bool IsBreakGlass { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A host-level membership (no single tenant).</summary>
    public bool IsHost => TenantId is null;
}

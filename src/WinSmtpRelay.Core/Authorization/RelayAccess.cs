using System.Security.Claims;

namespace WinSmtpRelay.Core.Authorization;

/// <summary>
/// Reads the membership claims off a principal and answers the access questions the authorization
/// policies need. Single place for the consent-based rule, reused by the authorization handler, the
/// tenant-scope resolver, and the UI. The rule:
/// <list type="bullet">
/// <item>A <b>host</b> membership grants host-level administration only — NOT automatic access to any
/// tenant's data.</item>
/// <item>Access to a specific tenant requires a <b>tenant membership</b> in that tenant (granted by a
/// tenant admin, or self-granted by a host admin as an audited break-glass). A host membership alone
/// never satisfies a tenant-scoped policy.</item>
/// <item>In <b>host scope</b> (a host admin with no active tenant selected) the tenant-scoped policies
/// pass so tenant pages can render their "select an organization" guard; no tenant is targeted.</item>
/// </list>
/// </summary>
public static class RelayAccess
{
    /// <summary>True when the principal holds a host membership.</summary>
    public static bool HasHostMembership(ClaimsPrincipal user)
        => user.HasClaim(RelayClaimTypes.IsHostAdmin, "true");

    /// <summary>Tenant id → role for every tenant membership the principal holds.</summary>
    public static IReadOnlyDictionary<int, string> TenantMemberships(ClaimsPrincipal user)
    {
        var map = new Dictionary<int, string>();
        foreach (var claim in user.FindAll(RelayClaimTypes.TenantMembership))
        {
            var sep = claim.Value.IndexOf(':');
            if (sep > 0 && int.TryParse(claim.Value.AsSpan(0, sep), out var tenantId))
                map[tenantId] = claim.Value[(sep + 1)..];
        }
        return map;
    }

    /// <summary>The explicitly selected active tenant (switcher / API-key scope), if any.</summary>
    public static int? ActiveTenant(ClaimsPrincipal user)
        => int.TryParse(user.FindFirst(RelayClaimTypes.ActiveTenant)?.Value, out var t) ? t : null;

    /// <summary>
    /// The tenant the principal is currently acting in for data/scope purposes, or null = host scope.
    /// Host membership + no active tenant ⇒ host scope. Otherwise the active tenant (if any), else the
    /// sole tenant membership (auto-scope), else null.
    /// </summary>
    public static int? CurrentScopeTenant(ClaimsPrincipal user)
    {
        var active = ActiveTenant(user);
        if (active is int a)
            return a;
        if (HasHostMembership(user))
            return null; // host scope (all tenants)
        var tenants = TenantMemberships(user);
        return tenants.Count == 1 ? tenants.Keys.First() : null;
    }

    /// <summary>Host-level administration (tenant lifecycle, server config, host admins).</summary>
    public static bool CanHostAdmin(ClaimsPrincipal user) => HasHostMembership(user);

    /// <summary>Read access to the current scope (tenant viewer/admin, or host scope for a host admin).</summary>
    public static bool CanView(ClaimsPrincipal user) => CanInScope(user, write: false);

    /// <summary>Read/write access to the current scope (tenant admin, or host scope for a host admin).</summary>
    public static bool CanFull(ClaimsPrincipal user) => CanInScope(user, write: true);

    private static bool CanInScope(ClaimsPrincipal user, bool write)
    {
        var active = ActiveTenant(user);
        if (active is int tenantId)
        {
            // A specific tenant is targeted: require a tenant membership in it (host membership alone
            // is NOT sufficient — consent model). Break-glass works because it creates such a membership.
            return TenantMemberships(user).TryGetValue(tenantId, out var role)
                && (write ? role == RelayRoles.TenantAdmin : role is RelayRoles.TenantAdmin or RelayRoles.TenantViewer);
        }

        // No specific tenant: host scope. A host admin may pass so the tenant pages render their
        // "select an organization" guard; a sole-tenant member is auto-scoped to their tenant.
        if (HasHostMembership(user))
            return true;
        var tenants = TenantMemberships(user);
        if (tenants.Count == 1)
        {
            var role = tenants.Values.First();
            return write ? role == RelayRoles.TenantAdmin : role is RelayRoles.TenantAdmin or RelayRoles.TenantViewer;
        }
        return false;
    }
}

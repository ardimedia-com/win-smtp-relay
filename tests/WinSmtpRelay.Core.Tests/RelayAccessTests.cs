using System.Security.Claims;
using WinSmtpRelay.Core.Authorization;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class RelayAccessTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static Claim Host() => new(RelayClaimTypes.IsHostAdmin, "true");
    private static Claim Member(int tenantId, string role) => new(RelayClaimTypes.TenantMembership, $"{tenantId}:{role}");
    private static Claim Active(int tenantId) => new(RelayClaimTypes.ActiveTenant, tenantId.ToString());

    [TestMethod, TestCategory("Unit")]
    public void HostMembership_DoesNotGrantTenantAccessWithoutMembership()
    {
        // The consent crux: a host admin scoped into a tenant they are NOT a member of has no access.
        var user = Principal(Host(), Active(7));
        Assert.IsFalse(RelayAccess.CanView(user), "host membership must not grant view of a non-member tenant");
        Assert.IsFalse(RelayAccess.CanFull(user), "host membership must not grant full of a non-member tenant");
        Assert.IsTrue(RelayAccess.CanHostAdmin(user), "host membership still grants host administration");
    }

    [TestMethod, TestCategory("Unit")]
    public void HostMembership_WithTenantMembership_GrantsThatTenant()
    {
        var user = Principal(Host(), Member(7, RelayRoles.TenantAdmin), Active(7));
        Assert.IsTrue(RelayAccess.CanFull(user));
        Assert.IsTrue(RelayAccess.CanView(user));
    }

    [TestMethod, TestCategory("Unit")]
    public void HostScope_NoActiveTenant_PassesTenantPoliciesForGuard()
    {
        // In host scope the tenant policies pass so tenant pages can render their "select org" guard.
        var user = Principal(Host());
        Assert.IsTrue(RelayAccess.CanFull(user));
        Assert.IsTrue(RelayAccess.CanView(user));
        Assert.IsTrue(RelayAccess.CanHostAdmin(user));
    }

    [TestMethod, TestCategory("Unit")]
    public void TenantAdmin_HasFull_NotHost()
    {
        var user = Principal(Member(3, RelayRoles.TenantAdmin), Active(3));
        Assert.IsTrue(RelayAccess.CanFull(user));
        Assert.IsTrue(RelayAccess.CanView(user));
        Assert.IsFalse(RelayAccess.CanHostAdmin(user));
    }

    [TestMethod, TestCategory("Unit")]
    public void TenantViewer_HasViewNotFull()
    {
        var user = Principal(Member(3, RelayRoles.TenantViewer), Active(3));
        Assert.IsTrue(RelayAccess.CanView(user));
        Assert.IsFalse(RelayAccess.CanFull(user));
    }

    [TestMethod, TestCategory("Unit")]
    public void TenantAdmin_CannotActInAnotherTenant()
    {
        // Member of tenant 3 only, but scoped (somehow) to tenant 9 → no access to 9.
        var user = Principal(Member(3, RelayRoles.TenantAdmin), Active(9));
        Assert.IsFalse(RelayAccess.CanView(user));
        Assert.IsFalse(RelayAccess.CanFull(user));
    }

    [TestMethod, TestCategory("Unit")]
    public void SingleTenantMembership_AutoScopes()
    {
        // No active-tenant claim, exactly one tenant membership → auto-scoped to it.
        var user = Principal(Member(4, RelayRoles.TenantAdmin));
        Assert.AreEqual(4, RelayAccess.CurrentScopeTenant(user));
        Assert.IsTrue(RelayAccess.CanFull(user));
    }

    [TestMethod, TestCategory("Unit")]
    public void TenantMemberships_ParsesAllClaims()
    {
        var user = Principal(Member(1, RelayRoles.TenantAdmin), Member(2, RelayRoles.TenantViewer));
        var map = RelayAccess.TenantMemberships(user);
        Assert.AreEqual(2, map.Count);
        Assert.AreEqual(RelayRoles.TenantAdmin, map[1]);
        Assert.AreEqual(RelayRoles.TenantViewer, map[2]);
    }
}

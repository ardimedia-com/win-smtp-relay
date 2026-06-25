using System.Security.Claims;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class TenantContextResolverTests
{
    private static ClaimsPrincipal Authenticated(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));

    [TestMethod]
    [TestCategory("Unit")]
    public void HostAdmin_WithoutActiveTenant_GetsHostScope()
    {
        var current = new CurrentTenant();
        TenantContextResolver.Apply(Authenticated(new Claim(RelayClaimTypes.IsHostAdmin, "true")), current);

        Assert.IsTrue(current.IsHostScope);
        Assert.IsFalse(current.FilterEnabled);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void HostAdmin_WithActiveTenant_IsScopedToThatTenant()
    {
        var current = new CurrentTenant();
        TenantContextResolver.Apply(
            Authenticated(new Claim(RelayClaimTypes.IsHostAdmin, "true"), new Claim(RelayClaimTypes.ActiveTenant, "5")),
            current);

        Assert.IsTrue(current.FilterEnabled);
        Assert.AreEqual(5, current.FilterTenantId);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TenantPrincipal_IsScopedToItsTenant()
    {
        var current = new CurrentTenant();
        // A tenant principal carries a tenant_membership claim ("{tenantId}:{role}"); with a single
        // membership and no active-tenant selection it is auto-scoped to that tenant.
        TenantContextResolver.Apply(Authenticated(new Claim(RelayClaimTypes.TenantMembership, "3:TenantAdmin")), current);

        Assert.IsTrue(current.FilterEnabled);
        Assert.AreEqual(3, current.FilterTenantId);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AuthenticatedWithoutTenantClaims_SeesNothing()
    {
        var current = new CurrentTenant();
        TenantContextResolver.Apply(Authenticated(new Claim(ClaimTypes.Name, "someone")), current);

        Assert.IsTrue(current.FilterEnabled);
        Assert.AreEqual(-1, current.FilterTenantId);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Unauthenticated_LeavesScopeUnset()
    {
        var current = new CurrentTenant();
        TenantContextResolver.Apply(new ClaimsPrincipal(new ClaimsIdentity()), current);

        Assert.IsFalse(current.FilterEnabled);
        Assert.IsFalse(current.IsHostScope);
    }
}

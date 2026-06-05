using System.Net;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.SmtpListener;
using Outcome = WinSmtpRelay.SmtpListener.UnauthenticatedTenantResolver.Outcome;

namespace WinSmtpRelay.SmtpListener.Tests;

[TestClass]
public class UnauthenticatedTenantResolverTests
{
    private const int Host = TenantDefaults.DefaultTenantId; // host baseline / default tenant
    private const int TenantA = 100;
    private const int TenantB = 200;

    private static IpAccessRule Allow(string network, int tenantId)
        => new() { Network = network, Action = IpAccessAction.Allow, TenantId = tenantId };

    private static (int?, bool) FromRules(string ip, params IpAccessRule[] rules)
        => UnauthenticatedTenantResolver.TenantFromAllowRules(IPAddress.Parse(ip), rules);

    // ----- TenantFromAllowRules -----

    [TestMethod]
    [TestCategory("Unit")]
    public void TenantFromAllowRules_SingleTenantMatch_Binds()
    {
        Assert.AreEqual((TenantA, false), FromRules("10.0.0.5", Allow("10.0.0.0/8", TenantA)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TenantFromAllowRules_HostBaselineRule_DoesNotBind()
    {
        // A default-tenant (host baseline) allow rule is shared and must not bind a specific tenant.
        Assert.AreEqual((null, false), FromRules("10.0.0.5", Allow("10.0.0.0/8", Host)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TenantFromAllowRules_TwoTenantsMatch_IsAmbiguous()
    {
        var (tenant, ambiguous) = FromRules("10.0.0.5", Allow("10.0.0.0/8", TenantA), Allow("10.0.0.0/16", TenantB));
        Assert.IsNull(tenant);
        Assert.IsTrue(ambiguous);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TenantFromAllowRules_NoMatch_ReturnsNull()
    {
        Assert.AreEqual((null, false), FromRules("203.0.113.1", Allow("10.0.0.0/8", TenantA)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TenantFromAllowRules_DenyRuleIgnored_ForBinding()
    {
        var rules = new[] { new IpAccessRule { Network = "10.0.0.0/8", Action = IpAccessAction.Deny, TenantId = TenantA } };
        Assert.AreEqual((null, false), UnauthenticatedTenantResolver.TenantFromAllowRules(IPAddress.Parse("10.0.0.5"), rules));
    }

    // ----- Decide -----

    [TestMethod]
    [TestCategory("Unit")]
    public void Decide_DefaultMode_AttributesBySenderDomain()
    {
        Assert.AreEqual((TenantA, Outcome.Resolved),
            UnauthenticatedTenantResolver.Decide(domainTenant: TenantA, ipTenant: null, ipAmbiguous: false,
                bindTenantToAllowIpRule: false, rejectUnresolvedTenant: false));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Decide_DefaultMode_UnclaimedDomain_FallsBackToDefaultTenant()
    {
        Assert.AreEqual((Host, Outcome.Resolved),
            UnauthenticatedTenantResolver.Decide(domainTenant: null, ipTenant: null, ipAmbiguous: false,
                bindTenantToAllowIpRule: false, rejectUnresolvedTenant: false));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Decide_RejectUnresolved_UnclaimedDomain_IsRejected()
    {
        Assert.AreEqual((null, Outcome.Unresolved),
            UnauthenticatedTenantResolver.Decide(domainTenant: null, ipTenant: null, ipAmbiguous: false,
                bindTenantToAllowIpRule: false, rejectUnresolvedTenant: true));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Decide_BindToIp_UsesIpTenant_WhenDomainUnclaimed()
    {
        Assert.AreEqual((TenantA, Outcome.Resolved),
            UnauthenticatedTenantResolver.Decide(domainTenant: null, ipTenant: TenantA, ipAmbiguous: false,
                bindTenantToAllowIpRule: true, rejectUnresolvedTenant: false));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Decide_BindToIp_MatchingDomainAndIp_Resolves()
    {
        Assert.AreEqual((TenantA, Outcome.Resolved),
            UnauthenticatedTenantResolver.Decide(domainTenant: TenantA, ipTenant: TenantA, ipAmbiguous: false,
                bindTenantToAllowIpRule: true, rejectUnresolvedTenant: false));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Decide_BindToIp_DomainOwnedByOtherTenant_IsRejectedAsSpoofing()
    {
        Assert.AreEqual((null, Outcome.CrossTenantDomain),
            UnauthenticatedTenantResolver.Decide(domainTenant: TenantB, ipTenant: TenantA, ipAmbiguous: false,
                bindTenantToAllowIpRule: true, rejectUnresolvedTenant: false));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Decide_BindToIp_AmbiguousIp_IsRejected()
    {
        Assert.AreEqual((null, Outcome.AmbiguousIp),
            UnauthenticatedTenantResolver.Decide(domainTenant: TenantA, ipTenant: null, ipAmbiguous: true,
                bindTenantToAllowIpRule: true, rejectUnresolvedTenant: false));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Decide_BindToIp_NoIpBinding_FallsBackToDomain()
    {
        // IP didn't bind a tenant (e.g. only host-baseline allowed); attribution falls back to the domain.
        Assert.AreEqual((TenantB, Outcome.Resolved),
            UnauthenticatedTenantResolver.Decide(domainTenant: TenantB, ipTenant: null, ipAmbiguous: false,
                bindTenantToAllowIpRule: true, rejectUnresolvedTenant: false));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Decide_BindToIp_NoIpBinding_UnclaimedDomain_RejectUnresolved()
    {
        Assert.AreEqual((null, Outcome.Unresolved),
            UnauthenticatedTenantResolver.Decide(domainTenant: null, ipTenant: null, ipAmbiguous: false,
                bindTenantToAllowIpRule: true, rejectUnresolvedTenant: true));
    }
}

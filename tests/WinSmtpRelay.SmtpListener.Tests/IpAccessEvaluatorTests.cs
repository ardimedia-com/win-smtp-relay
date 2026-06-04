using System.Net;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.SmtpListener;

namespace WinSmtpRelay.SmtpListener.Tests;

[TestClass]
public class IpAccessEvaluatorTests
{
    private static IpAccessRule Rule(string network, IpAccessAction action, int sortOrder)
        => new() { Network = network, Action = action, SortOrder = sortOrder };

    private static bool? Evaluate(string ip, params IpAccessRule[] rules)
        => IpAccessEvaluator.Evaluate(IPAddress.Parse(ip), rules);

    [TestMethod]
    [TestCategory("Unit")]
    public void NoRules_ReturnsNull_ForFallback()
    {
        Assert.IsNull(IpAccessEvaluator.Evaluate(IPAddress.Parse("10.0.0.1"), []));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AllowListMode_MatchingAllow_Permits()
    {
        Assert.AreEqual(true, Evaluate("10.0.0.5", Rule("10.0.0.0/8", IpAccessAction.Allow, 0)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AllowListMode_NoMatch_Blocks()
    {
        // An Allow rule exists, so an unmatched client is not permitted (allow-list semantics).
        Assert.AreEqual(false, Evaluate("203.0.113.1", Rule("10.0.0.0/8", IpAccessAction.Allow, 0)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DenyRule_BeforeAllow_Blocks()
    {
        // Deny (sort 0) wins over a broader Allow (sort 1) for an IP matching both.
        var result = Evaluate("10.1.2.3",
            Rule("10.1.2.0/24", IpAccessAction.Deny, 0),
            Rule("10.0.0.0/8", IpAccessAction.Allow, 1));
        Assert.AreEqual(false, result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Allow_BeforeDeny_Permits()
    {
        // First match wins: a more specific Allow placed first beats a later Deny.
        var result = Evaluate("10.1.2.3",
            Rule("10.1.2.0/24", IpAccessAction.Allow, 0),
            Rule("10.0.0.0/8", IpAccessAction.Deny, 1));
        Assert.AreEqual(true, result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DenyListMode_NoAllowRules_UnmatchedPermitted()
    {
        // Only Deny rules present: anything not denied is permitted (deny-list semantics).
        Assert.AreEqual(true, Evaluate("198.51.100.1", Rule("203.0.113.0/24", IpAccessAction.Deny, 0)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DenyListMode_MatchingDeny_Blocks()
    {
        Assert.AreEqual(false, Evaluate("203.0.113.5", Rule("203.0.113.0/24", IpAccessAction.Deny, 0)));
    }

    private static IpAccessRule TRule(int tenantId, string network, IpAccessAction action, int sortOrder)
        => new() { TenantId = tenantId, Network = network, Action = action, SortOrder = sortOrder };

    private static bool? EvaluateForTenant(string ip, int tenantId, params IpAccessRule[] rules)
        => IpAccessEvaluator.EvaluateForTenant(IPAddress.Parse(ip), rules, tenantId);

    [TestMethod]
    [TestCategory("Unit")]
    public void EvaluateForTenant_OtherTenantsDeny_IsIgnored()
    {
        // Tenant 2 denies 10.1.2.0/24; the host baseline (default tenant) allows 10.0.0.0/8.
        // For tenant 3, tenant 2's deny must not apply — the host allow permits.
        var result = EvaluateForTenant("10.1.2.3", tenantId: 3,
            TRule(2, "10.1.2.0/24", IpAccessAction.Deny, 1),
            TRule(TenantDefaults.DefaultTenantId, "10.0.0.0/8", IpAccessAction.Allow, 1));
        Assert.AreEqual(true, result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EvaluateForTenant_OwnDeny_Blocks_BeforeHostAllow()
    {
        // The tenant's own rules are evaluated before the host baseline.
        var result = EvaluateForTenant("10.1.2.3", tenantId: 2,
            TRule(2, "10.1.2.0/24", IpAccessAction.Deny, 1),
            TRule(TenantDefaults.DefaultTenantId, "10.0.0.0/8", IpAccessAction.Allow, 1));
        Assert.AreEqual(false, result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EvaluateForTenant_HostBaseline_AppliesToEveryTenant()
    {
        var rules = new[] { TRule(TenantDefaults.DefaultTenantId, "10.0.0.0/8", IpAccessAction.Allow, 1) };
        Assert.AreEqual(true, IpAccessEvaluator.EvaluateForTenant(IPAddress.Parse("10.0.0.5"), rules, 7));
        // Allow-list mode (from the host baseline) blocks an unmatched public IP for any tenant.
        Assert.AreEqual(false, IpAccessEvaluator.EvaluateForTenant(IPAddress.Parse("203.0.113.1"), rules, 7));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EvaluateForTenant_DefaultTenant_UsesBaselineOnly()
    {
        // Evaluating for the default tenant must ignore other tenants' rules entirely.
        var result = EvaluateForTenant("10.0.0.5", tenantId: TenantDefaults.DefaultTenantId,
            TRule(TenantDefaults.DefaultTenantId, "10.0.0.0/8", IpAccessAction.Allow, 1),
            TRule(2, "10.0.0.0/8", IpAccessAction.Deny, 0));
        Assert.AreEqual(true, result);
    }
}

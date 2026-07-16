using System.Net;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.SmtpListener;

namespace WinSmtpRelay.SmtpListener.Tests;

/// <summary>
/// The signal/noise split for observable rejections. If this returns true too readily, every port-25
/// scanner becomes a "finding" and the feature's whole point — telling a misconfigured known device apart
/// from background noise — collapses.
/// </summary>
[TestClass]
public class IpAccessEvaluatorTrustedSourceTests
{
    private static IpAccessRule Allow(string network, int tenantId = TenantDefaults.DefaultTenantId) =>
        new() { Network = network, Action = IpAccessAction.Allow, TenantId = tenantId, SortOrder = 0 };

    private static IpAccessRule Deny(string network, int tenantId = TenantDefaults.DefaultTenantId) =>
        new() { Network = network, Action = IpAccessAction.Deny, TenantId = tenantId, SortOrder = 0 };

    private static bool IsTrusted(string ip, IReadOnlyList<IpAccessRule> rules, params string[] staticNetworks) =>
        IpAccessEvaluator.IsTrustedSource(IPAddress.Parse(ip), rules, staticNetworks);

    [TestMethod]
    [TestCategory("Unit")]
    public void ClientInsideAnAllowRule_IsTrusted()
    {
        Assert.IsTrue(IsTrusted("10.0.0.5", [Allow("10.0.0.0/24")]));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ClientOutsideEveryAllowRule_IsNotTrusted()
    {
        Assert.IsFalse(IsTrusted("203.0.113.9", [Allow("10.0.0.0/24")]));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AnyNetworkAllowRule_DoesNotMakeTheWholeInternetTrusted()
    {
        // The point of the breadth guard: a single 0.0.0.0/0 allow rule must not turn every scanner on
        // the internet into a reportable finding.
        Assert.IsFalse(IsTrusted("203.0.113.9", [Allow("0.0.0.0/0")]));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void OverlyBroadAllowRule_DoesNotConferTrust()
    {
        // /1 is not "any", but two of them cover the address space — the same loophole IsTooBroadForRelay
        // closes for relaying, applied here so it cannot be used to flood the findings either.
        Assert.IsFalse(IsTrusted("203.0.113.9", [Allow("0.0.0.0/1"), Allow("128.0.0.0/1")]));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TrustIsTenantAgnostic_AnotherTenantsAllowRuleStillCounts()
    {
        // Asked at points where no tenant exists (a parser reject has no envelope; an attribution failure
        // is precisely a missing tenant), so ANY tenant's allow rule marks the client as configured.
        Assert.IsTrue(IsTrusted("10.0.0.5", [Allow("10.0.0.0/24", tenantId: 42)]));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DenyRule_DoesNotConferTrust()
    {
        Assert.IsFalse(IsTrusted("10.0.0.5", [Deny("10.0.0.0/24")]));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void StaticAllowedNetworks_ApplyOnlyAsAFallbackWhenNoDbRulesExist()
    {
        // Mirrors the acceptance gate's precedence: DB rules are authoritative, appsettings is the fallback.
        Assert.IsTrue(IsTrusted("192.168.1.5", [], "192.168.1.0/24"));
        Assert.IsFalse(IsTrusted("192.168.1.5", [Allow("10.0.0.0/24")], "192.168.1.0/24"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NoRulesAtAll_IsNotTrusted()
    {
        Assert.IsFalse(IsTrusted("10.0.0.5", []));
    }
}

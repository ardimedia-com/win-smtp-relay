using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class OutboundRoutingTests
{
    private static Tenant TenantWith(int id, bool enabled = true) => new() { Id = id, Name = $"t{id}", Slug = $"t{id}", IsEnabled = enabled };

    private static SendConnector Connector(int id, int tenantId, string? smartHost, bool isDefault = true, bool enabled = true) =>
        new() { Id = id, TenantId = tenantId, Name = $"c{id}", SmartHost = smartHost, IsDefault = isDefault, IsEnabled = enabled };

    [TestMethod]
    [TestCategory("Unit")]
    public void Evaluate_DefaultSmartHostConnector_IsSmartHostOnly()
    {
        var routing = OutboundRouting.Evaluate([TenantWith(1)], [Connector(2, 1, "smtp-relay.brevo.com")], [], null);

        Assert.IsTrue(routing.SmartHostOnly);
        Assert.AreEqual("smtp-relay.brevo.com", routing.SmartHostList);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Evaluate_NoConnectorNoGlobalSmartHost_DeliversDirectly()
    {
        var routing = OutboundRouting.Evaluate([TenantWith(1)], [Connector(1, 1, null)], [], null);

        Assert.IsTrue(routing.DirectMxPossible);
        Assert.IsFalse(routing.SmartHostOnly);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Evaluate_GlobalSmartHost_CoversTenantsWithoutOwnConnector()
    {
        var routing = OutboundRouting.Evaluate([TenantWith(1), TenantWith(2)], [Connector(1, 1, "a.example")], [], "global.example");

        Assert.IsTrue(routing.SmartHostOnly);
        CollectionAssert.AreEquivalent(new[] { "a.example", "global.example" }, routing.SmartHosts.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Evaluate_DisabledDefaultConnector_FallsBackToDirect()
    {
        var routing = OutboundRouting.Evaluate([TenantWith(1)], [Connector(1, 1, "a.example", enabled: false)], [], null);

        Assert.IsTrue(routing.DirectMxPossible);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Evaluate_DisabledTenant_IsIgnored()
    {
        var routing = OutboundRouting.Evaluate(
            [TenantWith(1), TenantWith(2, enabled: false)], [Connector(1, 1, "a.example")], [], null);

        Assert.IsTrue(routing.SmartHostOnly);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Evaluate_RouteToDirectConnector_MakesDirectPossible()
    {
        var direct = Connector(5, 1, null, isDefault: false);
        var routing = OutboundRouting.Evaluate(
            [TenantWith(1)], [Connector(1, 1, "a.example"), direct],
            [new DomainRoute { DomainPattern = "partner.example", SendConnectorId = 5, TenantId = 1 }], null);

        Assert.IsTrue(routing.DirectMxPossible);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("smtp-relay.brevo.com", "Brevo")]
    [DataRow("smtp-relay.sendinblue.com", "Brevo")]
    [DataRow("smtp.sendgrid.net", "SendGrid")]
    [DataRow("SMTP.OFFICE365.COM", "Microsoft 365")]
    public void SmartHostProviders_Find_KnownProvider(string host, string expected)
    {
        Assert.AreEqual(expected, SmartHostProviders.Find(host)?.Name);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("mail.example.com")]
    [DataRow("notbrevo.com")]
    [DataRow("")]
    public void SmartHostProviders_Find_UnknownHost_IsNull(string host)
    {
        Assert.IsNull(SmartHostProviders.Find(host));
    }
}

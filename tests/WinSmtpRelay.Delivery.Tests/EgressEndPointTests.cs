using WinSmtpRelay.Delivery;

namespace WinSmtpRelay.Delivery.Tests;

[TestClass]
public class EgressEndPointTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void ParseEgressEndPoint_ValidIPv4_ReturnsEndpointWithPortZero()
    {
        var ep = SmtpDeliveryService.ParseEgressEndPoint("203.0.113.10");
        Assert.IsNotNull(ep);
        Assert.AreEqual("203.0.113.10", ep!.Address.ToString());
        Assert.AreEqual(0, ep.Port);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ParseEgressEndPoint_ValidIPv6_ReturnsEndpoint()
    {
        var ep = SmtpDeliveryService.ParseEgressEndPoint("2001:db8::1");
        Assert.IsNotNull(ep);
        Assert.AreEqual(0, ep!.Port);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ParseEgressEndPoint_NullOrEmpty_ReturnsNull()
    {
        Assert.IsNull(SmtpDeliveryService.ParseEgressEndPoint(null));
        Assert.IsNull(SmtpDeliveryService.ParseEgressEndPoint("   "));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ParseEgressEndPoint_Invalid_ReturnsNull()
    {
        Assert.IsNull(SmtpDeliveryService.ParseEgressEndPoint("not-an-ip"));
        Assert.IsNull(SmtpDeliveryService.ParseEgressEndPoint("999.999.999.999"));
    }
}

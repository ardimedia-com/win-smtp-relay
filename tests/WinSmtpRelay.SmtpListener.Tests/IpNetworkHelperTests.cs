using System.Net;
using WinSmtpRelay.SmtpListener;

namespace WinSmtpRelay.SmtpListener.Tests;

[TestClass]
public class IpNetworkHelperTests
{
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("192.168.1.100", "192.168.0.0/16", true)]
    [DataRow("192.168.1.1", "192.168.1.0/24", true)]
    [DataRow("192.168.2.1", "192.168.1.0/24", false)]
    [DataRow("10.0.0.1", "10.0.0.0/8", true)]
    [DataRow("10.255.255.255", "10.0.0.0/8", true)]
    [DataRow("11.0.0.1", "10.0.0.0/8", false)]
    [DataRow("172.16.0.1", "172.16.0.0/12", true)]
    [DataRow("172.31.255.255", "172.16.0.0/12", true)]
    [DataRow("172.32.0.1", "172.16.0.0/12", false)]
    [DataRow("127.0.0.1", "127.0.0.1/32", true)]
    [DataRow("127.0.0.2", "127.0.0.1/32", false)]
    public void IsInNetwork_ReturnsExpectedResult(string ip, string cidr, bool expected)
    {
        var address = IPAddress.Parse(ip);
        var result = IpNetworkHelper.IsInNetwork(address, cidr);
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("10.0.0.0/")]      // empty prefix
    [DataRow("10.0.0.0/abc")]   // non-numeric prefix
    [DataRow("10.0.0.0/-1")]    // negative prefix
    [DataRow("10.0.0.0/33")]    // IPv4 prefix out of range (would index past the byte array)
    [DataRow("10.0.0.0/40")]
    [DataRow("not-an-ip/24")]   // malformed network address
    public void IsInNetwork_MalformedCidr_ReturnsFalse_WithoutThrowing(string cidr)
    {
        // Admin-entered CIDR runs on the SMTP hot path — a bad rule must never throw, only not match.
        Assert.IsFalse(IpNetworkHelper.IsInNetwork(IPAddress.Parse("10.0.0.5"), cidr));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsInAnyNetwork_ReturnsTrueWhenMatchFound()
    {
        var address = IPAddress.Parse("192.168.1.50");
        var networks = new[] { "10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12" };
        Assert.IsTrue(IpNetworkHelper.IsInAnyNetwork(address, networks));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsInAnyNetwork_ReturnsFalseWhenNoMatch()
    {
        var address = IPAddress.Parse("8.8.8.8");
        var networks = new[] { "10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12" };
        Assert.IsFalse(IpNetworkHelper.IsInAnyNetwork(address, networks));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsInNetwork_HandlesIpv4MappedToIpv6()
    {
        var address = IPAddress.Parse("192.168.1.1").MapToIPv6();
        Assert.IsTrue(IpNetworkHelper.IsInNetwork(address, "192.168.0.0/16"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsInNetwork_HandlesCidrWithoutPrefix()
    {
        var address = IPAddress.Parse("10.0.0.1");
        // No prefix = /32 for IPv4
        Assert.IsTrue(IpNetworkHelper.IsInNetwork(address, "10.0.0.1"));
        Assert.IsFalse(IpNetworkHelper.IsInNetwork(address, "10.0.0.2"));
    }
}

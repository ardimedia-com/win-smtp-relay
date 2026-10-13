using System.Net;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class IpAddressScopeTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void PrivateAndLoopbackAddresses_AreRecognised()
    {
        foreach (var ip in new[] { "10.1.1.122", "172.16.0.1", "192.168.0.64", "127.0.0.1", "169.254.1.1", "100.64.0.1", "::1", "fd00::1", "fe80::1" })
            Assert.IsTrue(IpAddressScope.IsPrivateOrLoopback(IPAddress.Parse(ip)), ip);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PublicAddresses_AreNotPrivate()
    {
        foreach (var ip in new[] { "85.31.156.82", "8.8.8.8", "172.32.0.1", "2001:4860:4860::8888" })
            Assert.IsFalse(IpAddressScope.IsPrivateOrLoopback(IPAddress.Parse(ip)), ip);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TryParseAddressOrNetwork_AcceptsAddressesAndCidr_RejectsGarbage()
    {
        Assert.IsTrue(IpAddressScope.TryParseAddressOrNetwork("85.31.156.82", out _));
        Assert.IsTrue(IpAddressScope.TryParseAddressOrNetwork("85.31.156.0/24", out var net));
        Assert.AreEqual(IPAddress.Parse("85.31.156.0"), net);
        Assert.IsTrue(IpAddressScope.TryParseAddressOrNetwork("2001:db8::/48", out _));
        Assert.IsFalse(IpAddressScope.TryParseAddressOrNetwork("smtp.example.com", out _));
        Assert.IsFalse(IpAddressScope.TryParseAddressOrNetwork("10.0.0.0/33", out _));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Loopback_IsAssignedLocally_DocumentationAddressIsNot()
    {
        Assert.IsTrue(IpAddressScope.IsAssignedLocally(IPAddress.Loopback));
        Assert.IsFalse(IpAddressScope.IsAssignedLocally(IPAddress.Parse("203.0.113.250")));
    }
}

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;

namespace WinSmtpRelay.Security.Tests;

[TestClass]
public class DnsSetupServiceTests
{
    // The DNS page derives the expected DKIM record from the stored PRIVATE key. Verify that
    // RSA.ImportFromPem + ExportSubjectPublicKeyInfo reproduces exactly what DkimKeyGenerator
    // publishes, so live-vs-expected comparison is correct.
    [TestMethod]
    [TestCategory("Unit")]
    public void DkimDerivation_FromPrivateKey_MatchesGeneratorDnsValue()
    {
        var (privatePem, _, generatorDnsTxt) = DkimKeyGenerator.GenerateKeyPair("example.com", "s1", 2048);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privatePem);
        var derived = "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        Assert.AreEqual(generatorDnsTxt, derived);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildOwnershipRecord_PrefixesToken()
    {
        var service = new DnsSetupService(null!, Options.Create(new DnsOptions()), null!, null!, NullLogger<DnsSetupService>.Instance);
        Assert.AreEqual("winsmtprelay-verification=abc123", service.BuildOwnershipRecord("abc123"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CheckOwnershipAsync_EmptyToken_ReturnsFalseWithoutDnsLookup()
    {
        // null DNS client — an empty token must short-circuit before any lookup.
        var service = new DnsSetupService(null!, Options.Create(new DnsOptions()), null!, null!, NullLogger<DnsSetupService>.Instance);
        Assert.IsFalse(await service.CheckOwnershipAsync("example.com", ""));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildRecommendedSpf_EmitsIpHostnameIncludesAndQualifier()
    {
        var options = new DnsOptions
        {
            SendingIpAddresses = ["203.0.113.10"],
            PublicHostname = "relay.example.com",
            SpfIncludes = ["_spf.example.net"],
            SpfAllQualifier = "~all"
        };
        var service = new DnsSetupService(null!, Options.Create(options), null!, null!, NullLogger<DnsSetupService>.Instance);

        Assert.AreEqual("v=spf1 ip4:203.0.113.10 a:relay.example.com include:_spf.example.net ~all", service.BuildRecommendedSpf());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildRecommendedSpf_Ipv6UsesIp6Mechanism()
    {
        var options = new DnsOptions { SendingIpAddresses = ["2001:db8::1"], SpfAllQualifier = "-all" };
        var service = new DnsSetupService(null!, Options.Create(options), null!, null!, NullLogger<DnsSetupService>.Instance);

        Assert.AreEqual("v=spf1 ip6:2001:db8::1 -all", service.BuildRecommendedSpf());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildRecommendedDmarc_UsesPolicyRuaAndPercentage()
    {
        var options = new DnsOptions { DmarcPolicy = "quarantine", DmarcReportEmail = "dmarc@example.com", DmarcPercentage = 50 };
        var service = new DnsSetupService(null!, Options.Create(options), null!, null!, NullLogger<DnsSetupService>.Instance);

        Assert.AreEqual("v=DMARC1; p=quarantine; rua=mailto:dmarc@example.com; pct=50", service.BuildRecommendedDmarc("example.com"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildRecommendedDmarc_OmitsRuaWhenNoEmail()
    {
        var options = new DnsOptions { DmarcPolicy = "none", DmarcReportEmail = null, DmarcPercentage = 100 };
        var service = new DnsSetupService(null!, Options.Create(options), null!, null!, NullLogger<DnsSetupService>.Instance);

        Assert.AreEqual("v=DMARC1; p=none; pct=100", service.BuildRecommendedDmarc("example.com"));
    }
}

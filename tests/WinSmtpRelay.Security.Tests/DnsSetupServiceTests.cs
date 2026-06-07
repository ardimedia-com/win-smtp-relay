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
        var service = new DnsSetupService(null!, null!, Options.Create(new DnsOptions()), null!, null!, null!, NullLogger<DnsSetupService>.Instance);
        Assert.AreEqual("winsmtprelay-verification=abc123", service.BuildOwnershipRecord("abc123"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CheckOwnershipAsync_EmptyToken_ReturnsFalseWithoutDnsLookup()
    {
        // null DNS client — an empty token must short-circuit before any lookup.
        var service = new DnsSetupService(null!, null!, Options.Create(new DnsOptions()), null!, null!, null!, NullLogger<DnsSetupService>.Instance);
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
        var service = new DnsSetupService(null!, null!, Options.Create(options), null!, null!, null!, NullLogger<DnsSetupService>.Instance);

        Assert.AreEqual("v=spf1 ip4:203.0.113.10 a:relay.example.com include:_spf.example.net ~all", service.BuildRecommendedSpf());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildRecommendedSpf_Ipv6UsesIp6Mechanism()
    {
        var options = new DnsOptions { SendingIpAddresses = ["2001:db8::1"], SpfAllQualifier = "-all" };
        var service = new DnsSetupService(null!, null!, Options.Create(options), null!, null!, null!, NullLogger<DnsSetupService>.Instance);

        Assert.AreEqual("v=spf1 ip6:2001:db8::1 -all", service.BuildRecommendedSpf());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildRecommendedDmarc_UsesPolicyRuaAndPercentage()
    {
        var options = new DnsOptions { DmarcPolicy = "quarantine", DmarcReportEmail = "dmarc@example.com", DmarcPercentage = 50 };
        var service = new DnsSetupService(null!, null!, Options.Create(options), null!, null!, null!, NullLogger<DnsSetupService>.Instance);

        Assert.AreEqual("v=DMARC1; p=quarantine; rua=mailto:dmarc@example.com; pct=50", service.BuildRecommendedDmarc("example.com"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildRecommendedDmarc_OmitsRuaWhenNoEmail()
    {
        var options = new DnsOptions { DmarcPolicy = "none", DmarcReportEmail = null, DmarcPercentage = 100 };
        var service = new DnsSetupService(null!, null!, Options.Create(options), null!, null!, null!, NullLogger<DnsSetupService>.Instance);

        Assert.AreEqual("v=DMARC1; p=none; pct=100", service.BuildRecommendedDmarc("example.com"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildMergedSpf_InsertsMissingMechanismsBeforeAll_KeepingPublishedSendersAndQualifier()
    {
        // The published record already authorises Outlook and Mandrill; merging must keep them (a domain
        // may have only one SPF record) and add only the relay's missing parts before the "all".
        var live = "v=spf1 a mx ip4:85.31.156.82 include:spf.protection.outlook.com include:spf.mandrillapp.com ~all";
        var expected = "v=spf1 ip4:178.197.238.240 a:smtp2.ardimedia.com ~all";

        var merged = DnsSetupService.BuildMergedSpf(live, expected);

        Assert.AreEqual(
            "v=spf1 a mx ip4:85.31.156.82 include:spf.protection.outlook.com include:spf.mandrillapp.com ip4:178.197.238.240 a:smtp2.ardimedia.com ~all",
            merged);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildMergedSpf_DoesNotDuplicateMechanismsAlreadyPublished()
    {
        var live = "v=spf1 ip4:203.0.113.10 include:_spf.example.net ~all";
        var expected = "v=spf1 ip4:203.0.113.10 a:relay.example.com include:_spf.example.net ~all";

        var merged = DnsSetupService.BuildMergedSpf(live, expected);

        // Only the missing a: mechanism is added; the shared ip4/include are not repeated.
        Assert.AreEqual("v=spf1 ip4:203.0.113.10 include:_spf.example.net a:relay.example.com ~all", merged);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildMergedSpf_KeepsPublishedAllQualifier_NotTheRecommendedOne()
    {
        var live = "v=spf1 ip4:85.31.156.82 -all";   // published uses hardfail
        var expected = "v=spf1 ip4:178.197.238.240 ~all"; // recommended uses softfail

        var merged = DnsSetupService.BuildMergedSpf(live, expected);

        Assert.AreEqual("v=spf1 ip4:85.31.156.82 ip4:178.197.238.240 -all", merged);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildMergedSpf_AppendsWhenPublishedRecordHasNoAll()
    {
        var live = "v=spf1 ip4:85.31.156.82";   // no terminal "all"
        var expected = "v=spf1 ip4:178.197.238.240 ~all";

        var merged = DnsSetupService.BuildMergedSpf(live, expected);

        Assert.AreEqual("v=spf1 ip4:85.31.156.82 ip4:178.197.238.240", merged);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildMergedSpf_ReturnsPublishedUnchanged_WhenNothingMissing()
    {
        var live = "v=spf1 a mx ip4:178.197.238.240 a:smtp2.ardimedia.com ~all";
        var expected = "v=spf1 ip4:178.197.238.240 a:smtp2.ardimedia.com ~all";

        Assert.AreEqual(live, DnsSetupService.BuildMergedSpf(live, expected));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CountSpfLookupTerms_CountsLookupMechanismsAndRedirect_IgnoresIpAndAll()
    {
        // a(1) mx(1) include(1) include(1) = 4; ip4/ip6/all/v= cost nothing.
        Assert.AreEqual(4, DnsSetupService.CountSpfLookupTerms(
            "v=spf1 a mx ip4:85.31.156.82 ip6:2001:db8::1 include:spf.protection.outlook.com include:spf.mandrillapp.com ~all"));
        // redirect= modifier counts as one lookup.
        Assert.AreEqual(1, DnsSetupService.CountSpfLookupTerms("v=spf1 redirect=_spf.example.com"));
        // ip4/ip6/all only — zero lookups.
        Assert.AreEqual(0, DnsSetupService.CountSpfLookupTerms("v=spf1 ip4:203.0.113.10 ip6:2001:db8::1 -all"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CountSpfLookupTerms_HandlesQualifiersAndScopedMechanisms()
    {
        // -include, ?exists, ~mx, +a with a CIDR all count; ptr counts; the leading qualifier is ignored.
        Assert.AreEqual(5, DnsSetupService.CountSpfLookupTerms(
            "v=spf1 +a/24 ~mx -include:_spf.example.net ?exists:%{i}.example.com ptr -all"));
    }
}

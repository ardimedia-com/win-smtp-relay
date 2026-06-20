using System.Net;
using DnsClient;
using Microsoft.Extensions.Logging.Abstractions;
using WinSmtpRelay.Security;
using WinSmtpRelay.Security.Models;

namespace WinSmtpRelay.Security.Tests;

[TestClass]
public class DmarcValidatorTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task CheckAsync_NoDomain_ReturnsNone()
    {
        var dns = new LookupClient();
        var validator = new DmarcValidator(dns, new PublicSuffixService(), NullLogger<DmarcValidator>.Instance);

        var spf = new SpfCheckResult(SpfVerdict.Pass, "test");
        var result = await validator.CheckAsync("", "example.com", spf);
        Assert.AreEqual(DmarcVerdict.None, result.Verdict);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task CheckAsync_GoogleDomain_ReturnsResult()
    {
        var dns = new LookupClient();
        var validator = new DmarcValidator(dns, new PublicSuffixService(), NullLogger<DmarcValidator>.Instance);

        var spf = new SpfCheckResult(SpfVerdict.Pass, "test");
        var result = await validator.CheckAsync("google.com", "google.com", spf);
        // Google has a DMARC record
        Assert.AreNotEqual(DmarcVerdict.None, result.Verdict);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task CheckAsync_NonExistentDomain_ReturnsNone()
    {
        var dns = new LookupClient();
        var validator = new DmarcValidator(dns, new PublicSuffixService(), NullLogger<DmarcValidator>.Instance);

        var spf = new SpfCheckResult(SpfVerdict.Pass, "test");
        var result = await validator.CheckAsync("nonexistent-domain-12345.invalid", "nonexistent-domain-12345.invalid", spf);
        Assert.AreEqual(DmarcVerdict.None, result.Verdict);
    }
}

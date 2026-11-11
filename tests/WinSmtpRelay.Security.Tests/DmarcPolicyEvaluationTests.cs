using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WinSmtpRelay.Security.Models;

namespace WinSmtpRelay.Security.Tests;

/// <summary>
/// M5: the DMARC verdict carries the policy that APPLIES to the From domain (p= or sp=) and the record's
/// pct=, and a DNS failure is reported as TempError, never as "no record".
/// </summary>
[TestClass]
public class DmarcPolicyEvaluationTests
{
    private static readonly SpfCheckResult SpfFail = new(SpfVerdict.Fail, "test");

    private static IDnsQueryResponse Txt(string name, string? value)
    {
        var r = Substitute.For<IDnsQueryResponse>();
        r.HasError.Returns(false);
        r.Answers.Returns(value is null
            ? Array.Empty<DnsResourceRecord>()
            : [new TxtRecord(new ResourceRecordInfo(name, ResourceRecordType.TXT, QueryClass.IN, 300, 0), [value], [value])]);
        return r;
    }

    private static IDnsQueryResponse Error(DnsHeaderResponseCode code)
    {
        var r = Substitute.For<IDnsQueryResponse>();
        r.HasError.Returns(true);
        r.Header.Returns(new DnsResponseHeader(1, (ushort)code, 1, 0, 0, 0));
        r.Answers.Returns(Array.Empty<DnsResourceRecord>());
        return r;
    }

    /// <summary>A resolver that answers from a map of name to response (unknown names: NXDOMAIN).</summary>
    private static ILookupClient Dns(Dictionary<string, IDnsQueryResponse> answers)
    {
        var client = Substitute.For<ILookupClient>();
        client.QueryAsync(Arg.Any<string>(), Arg.Any<QueryType>(), Arg.Any<QueryClass>(), Arg.Any<CancellationToken>())
            .Returns(call => answers.TryGetValue(call.ArgAt<string>(0), out var r) ? r : Error(DnsHeaderResponseCode.NotExistentDomain));
        return client;
    }

    private static DmarcValidator Validator(ILookupClient dns) =>
        new(dns, new PublicSuffixService(), NullLogger<DmarcValidator>.Instance);

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RecordAtFromDomain_UsesP()
    {
        var dns = Dns(new() { ["_dmarc.example.com"] = Txt("_dmarc.example.com", "v=DMARC1; p=reject; sp=none") });

        var result = await Validator(dns).CheckAsync("example.com", "other.net", SpfFail);

        Assert.AreEqual(DmarcVerdict.Fail, result.Verdict);
        Assert.AreEqual(DmarcPolicy.Reject, result.Policy);
        Assert.AreEqual(100, result.Percentage);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SubdomainWithoutRecord_UsesOrgDomainSp()
    {
        var dns = Dns(new() { ["_dmarc.example.com"] = Txt("_dmarc.example.com", "v=DMARC1; p=reject; sp=quarantine") });

        var result = await Validator(dns).CheckAsync("mail.example.com", "other.net", SpfFail);

        Assert.AreEqual(DmarcVerdict.Fail, result.Verdict);
        Assert.AreEqual(DmarcPolicy.Quarantine, result.Policy, "a subdomain is governed by sp=");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SubdomainWithoutSp_InheritsP()
    {
        var dns = Dns(new() { ["_dmarc.example.com"] = Txt("_dmarc.example.com", "v=DMARC1; p=reject") });

        var result = await Validator(dns).CheckAsync("mail.example.com", "other.net", SpfFail);

        Assert.AreEqual(DmarcPolicy.Reject, result.Policy);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Pct_IsCarried()
    {
        var dns = Dns(new() { ["_dmarc.example.com"] = Txt("_dmarc.example.com", "v=DMARC1; p=reject; pct=25") });

        var result = await Validator(dns).CheckAsync("example.com", "other.net", SpfFail);

        Assert.AreEqual(25, result.Percentage);
        Assert.IsFalse(new AuthenticationResults(SpfFail, result).ShouldReject, "pct below 100 never rejects outright");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NoRecordAnywhere_IsNone()
    {
        var result = await Validator(Dns([])).CheckAsync("example.com", "example.com", SpfFail);

        Assert.AreEqual(DmarcVerdict.None, result.Verdict);
        Assert.IsTrue(new AuthenticationResults(SpfFail, result).ShouldReject, "no DMARC record: SPF fail alone rejects");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ServerFailure_IsTempError_NotNone()
    {
        var dns = Dns(new() { ["_dmarc.example.com"] = Error(DnsHeaderResponseCode.ServerFailure) });

        var result = await Validator(dns).CheckAsync("example.com", "example.com", SpfFail);

        Assert.AreEqual(DmarcVerdict.TempError, result.Verdict);
        var decision = new AuthenticationResults(SpfFail, result);
        Assert.IsFalse(decision.ShouldReject);
        Assert.IsTrue(decision.ShouldDefer);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Timeout_IsTempError()
    {
        var dns = Substitute.For<ILookupClient>();
        dns.QueryAsync(Arg.Any<string>(), Arg.Any<QueryType>(), Arg.Any<QueryClass>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DnsResponseException(DnsResponseCode.ConnectionTimeout, "timed out"));

        var result = await Validator(dns).CheckAsync("example.com", "example.com", SpfFail);

        Assert.AreEqual(DmarcVerdict.TempError, result.Verdict);
    }
}

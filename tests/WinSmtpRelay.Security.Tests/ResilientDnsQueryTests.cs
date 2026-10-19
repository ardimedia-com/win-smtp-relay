using System.Net;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Security.Tests;

/// <summary>
/// Deliverability DNS lookups (T245): a failed query falls back to the system resolver, and when nobody answers
/// the check says "could not verify" — never "record not published".
/// </summary>
[TestClass]
public class ResilientDnsQueryTests
{
    private static IDnsQueryResponse Answer(params DnsResourceRecord[] records)
    {
        var r = Substitute.For<IDnsQueryResponse>();
        r.HasError.Returns(false);
        r.Answers.Returns(records);
        return r;
    }

    private static IDnsQueryResponse Error(DnsHeaderResponseCode code)
    {
        var r = Substitute.For<IDnsQueryResponse>();
        r.HasError.Returns(true);
        r.ErrorMessage.Returns(code.ToString());
        r.Header.Returns(new DnsResponseHeader(1, (ushort)code, 1, 0, 0, 0));
        r.Answers.Returns(Array.Empty<DnsResourceRecord>());
        return r;
    }

    private static MxRecord Mx(string domain, string exchange) =>
        new(new ResourceRecordInfo(domain, ResourceRecordType.MX, QueryClass.IN, 300, 0), 10, DnsString.Parse(exchange));

    private static ILookupClient Timeout()
    {
        var client = Substitute.For<ILookupClient>();
        client.QueryAsync(Arg.Any<string>(), Arg.Any<QueryType>(), Arg.Any<QueryClass>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DnsResponseException(DnsResponseCode.ConnectionTimeout, "timed out"));
        return client;
    }

    private static ILookupClient Answering(IDnsQueryResponse response)
    {
        var client = Substitute.For<ILookupClient>();
        client.QueryAsync(Arg.Any<string>(), Arg.Any<QueryType>(), Arg.Any<QueryClass>(), Arg.Any<CancellationToken>())
            .Returns(response);
        return client;
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PublicResolversTimeOut_SystemResolverAnswers()
    {
        var system = Answering(Answer(Mx("example.com", "mx.example.com.")));
        var query = new ResilientDnsQuery(Timeout(), system, NullLogger.Instance);

        var response = await query.QueryAsync("example.com", QueryType.MX, CancellationToken.None);

        Assert.AreEqual(1, response.Answers.Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ServFail_IsNotAnAnswer_NxDomainIs()
    {
        var query = new ResilientDnsQuery(Answering(Error(DnsHeaderResponseCode.ServerFailure)), Timeout(), NullLogger.Instance);
        var ex = await Assert.ThrowsExactlyAsync<DnsLookupFailedException>(() => query.QueryAsync("example.com", QueryType.TXT, CancellationToken.None));
        StringAssert.Contains(ex.Reason, "timeout");

        // NXDOMAIN settles the question (the name does not exist) — no fallback, no failure.
        var system = Substitute.For<ILookupClient>();
        var nx = new ResilientDnsQuery(Answering(Error(DnsHeaderResponseCode.NotExistentDomain)), system, NullLogger.Instance);
        var response = await nx.QueryAsync("nope.example.com", QueryType.TXT, CancellationToken.None);
        Assert.IsTrue(response.HasError);
        await system.DidNotReceiveWithAnyArgs().QueryAsync(default!, default, default, default);
    }

    private static DnsSetupService Service(ILookupClient system, ILookupClient publicResolvers)
    {
        var settings = Substitute.For<IDnsSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new DnsSettings { PublicHostname = "relay.example.com" });
        return new DnsSetupService(system, PublicDnsLookupClient.Wrapping(publicResolvers), Options.Create(new DnsOptions()),
            null!, null!, settings, null!, null!, Options.Create(new DeliveryOptions()), NullLogger<DnsSetupService>.Instance);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CheckMx_NoResolverAnswers_IsUnverified_NotMissing()
    {
        var result = await Service(Timeout(), Timeout()).CheckMxAsync("example.com");

        Assert.AreEqual(DnsRecordStatus.Unverified, result.Status);
        StringAssert.StartsWith(result.Explanation, "Could not verify");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CheckMx_PublicFails_SystemAnswers_UsesTheAnswer()
    {
        var result = await Service(Answering(Answer(Mx("example.com", "relay.example.com."))), Timeout()).CheckMxAsync("example.com");

        Assert.AreEqual(DnsRecordStatus.Ok, result.Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CheckMx_EmptyAnswer_IsStillMissing()
    {
        var result = await Service(Timeout(), Answering(Answer())).CheckMxAsync("example.com");

        Assert.AreEqual(DnsRecordStatus.Missing, result.Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DmarcAlignment_UnverifiedDkim_IsUnverified_NotFail()
    {
        var unverified = new DnsRecordResult("DKIM", "s1._domainkey.example.com", "v=DKIM1", null, DnsRecordStatus.Unverified, "Could not verify");
        var missingSpf = new DnsRecordResult("SPF", "example.com", "v=spf1", null, DnsRecordStatus.Missing, "none");
        var dmarc = new DnsRecordResult("DMARC", "_dmarc.example.com", "v=DMARC1", "v=DMARC1; p=none", DnsRecordStatus.Ok, "ok");

        Assert.AreEqual(DmarcAlignmentVerdict.Unverified, DmarcAlignment.Evaluate(missingSpf, unverified, dmarc).Verdict);

        // Smart host: a failed selector lookup is not "no key published".
        var smartHost = new SmartHostDkim("smtp-relay.brevo.com", "Brevo", null, ["brevo1", "brevo2"], LookupFailed: true);
        var notConfigured = new DnsRecordResult("DKIM", "x", "", null, DnsRecordStatus.NotConfigured, "none");
        Assert.AreEqual(DmarcAlignmentVerdict.Unverified, DmarcAlignment.Evaluate(missingSpf, notConfigured, dmarc, smartHost).Verdict);
        Assert.AreEqual(DmarcAlignmentVerdict.Fail, DmarcAlignment.Evaluate(missingSpf, notConfigured, dmarc, smartHost with { LookupFailed = false }).Verdict);
    }
}

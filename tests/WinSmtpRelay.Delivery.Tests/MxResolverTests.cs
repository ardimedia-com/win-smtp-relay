using System.Net;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Delivery.Tests;

/// <summary>M11: DNS verdicts — a domain that cannot receive mail fails permanently, a DNS outage temporarily.</summary>
[TestClass]
public class MxResolverTests
{
    private static IDnsQueryResponse Response(DnsHeaderResponseCode code, params DnsResourceRecord[] answers)
    {
        var response = Substitute.For<IDnsQueryResponse>();
        response.Answers.Returns(answers);
        response.HasError.Returns(code != DnsHeaderResponseCode.NoError);
        response.ErrorMessage.Returns(code.ToString());
        // The response code lives in the low 4 bits of the header flags.
        response.Header.Returns(new DnsResponseHeader(1, (ushort)code, 1, answers.Length, 0, 0));
        return response;
    }

    private static MxRecord Mx(string domain, ushort preference, string exchange) =>
        new(new ResourceRecordInfo(domain, ResourceRecordType.MX, QueryClass.IN, 300, 0), preference, DnsString.Parse(exchange));

    private static ARecord A(string host, string ip) =>
        new(new ResourceRecordInfo(host, ResourceRecordType.A, QueryClass.IN, 300, 0), IPAddress.Parse(ip));

    private static (MxResolver Resolver, ILookupClient Dns) Create()
    {
        var dns = Substitute.For<ILookupClient>();
        // Default: an empty, successful answer for anything not set up explicitly.
        dns.QueryAsync(Arg.Any<string>(), Arg.Any<QueryType>(), Arg.Any<QueryClass>(), Arg.Any<CancellationToken>())
            .Returns(_ => Response(DnsHeaderResponseCode.NoError));
        return (new MxResolver(dns, NullLogger<MxResolver>.Instance), dns);
    }

    private static void Setup(ILookupClient dns, string name, QueryType type, IDnsQueryResponse response) =>
        dns.QueryAsync(name, type, Arg.Any<QueryClass>(), Arg.Any<CancellationToken>()).Returns(response);

    [TestMethod]
    [TestCategory("Unit")]
    public async Task MxRecords_OrderedByPreference()
    {
        var (resolver, dns) = Create();
        Setup(dns, "a.test", QueryType.MX, Response(DnsHeaderResponseCode.NoError,
            Mx("a.test", 20, "backup.a.test."), Mx("a.test", 10, "primary.a.test.")));

        var hosts = await resolver.ResolveMxAsync("a.test");

        CollectionAssert.AreEqual(new[] { "primary.a.test", "backup.a.test" }, hosts.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EqualPreference_IsRandomised()
    {
        var (resolver, dns) = Create();
        Setup(dns, "a.test", QueryType.MX, Response(DnsHeaderResponseCode.NoError,
            Mx("a.test", 10, "one.a.test."), Mx("a.test", 10, "two.a.test."), Mx("a.test", 20, "last.a.test.")));

        var firsts = new HashSet<string>();
        for (var i = 0; i < 64; i++)
        {
            var hosts = await resolver.ResolveMxAsync("a.test");
            Assert.AreEqual("last.a.test", hosts[^1], "a higher preference value always comes last");
            firsts.Add(hosts[0]);
        }
        Assert.AreEqual(2, firsts.Count, "both equal-preference hosts must come first sometimes");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NullMx_IsPermanent()
    {
        var (resolver, dns) = Create();
        Setup(dns, "a.test", QueryType.MX, Response(DnsHeaderResponseCode.NoError, Mx("a.test", 0, ".")));

        var ex = await Assert.ThrowsExactlyAsync<MxLookupException>(() => resolver.ResolveMxAsync("a.test"));
        Assert.IsTrue(ex.IsPermanent);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NxDomain_IsPermanent()
    {
        var (resolver, dns) = Create();
        Setup(dns, "a.test", QueryType.MX, Response(DnsHeaderResponseCode.NotExistentDomain));

        var ex = await Assert.ThrowsExactlyAsync<MxLookupException>(() => resolver.ResolveMxAsync("a.test"));
        Assert.IsTrue(ex.IsPermanent);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ServFail_IsTemporary()
    {
        var (resolver, dns) = Create();
        Setup(dns, "a.test", QueryType.MX, Response(DnsHeaderResponseCode.ServerFailure));

        var ex = await Assert.ThrowsExactlyAsync<MxLookupException>(() => resolver.ResolveMxAsync("a.test"));
        Assert.IsFalse(ex.IsPermanent);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Timeout_IsTemporary()
    {
        var (resolver, dns) = Create();
        dns.QueryAsync("a.test", QueryType.MX, Arg.Any<QueryClass>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DnsResponseException(DnsResponseCode.ConnectionTimeout));

        var ex = await Assert.ThrowsExactlyAsync<MxLookupException>(() => resolver.ResolveMxAsync("a.test"));
        Assert.IsFalse(ex.IsPermanent);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NoMx_WithAddress_FallsBackToTheDomain()
    {
        var (resolver, dns) = Create();
        Setup(dns, "a.test", QueryType.A, Response(DnsHeaderResponseCode.NoError, A("a.test", "198.51.100.1")));

        CollectionAssert.AreEqual(new[] { "a.test" }, (await resolver.ResolveMxAsync("a.test")).ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NoMx_NoAddress_IsPermanent()
    {
        var (resolver, _) = Create();

        var ex = await Assert.ThrowsExactlyAsync<MxLookupException>(() => resolver.ResolveMxAsync("a.test"));
        Assert.IsTrue(ex.IsPermanent);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task HostAddresses_IpLiteral_ResolvesToItself()
    {
        var (resolver, _) = Create();
        var addresses = await resolver.ResolveHostAddressesAsync("[192.0.2.5]");
        Assert.AreEqual(IPAddress.Parse("192.0.2.5"), addresses.Single());
    }
}

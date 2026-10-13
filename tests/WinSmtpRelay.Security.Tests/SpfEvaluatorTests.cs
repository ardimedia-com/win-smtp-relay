using System.Net;

namespace WinSmtpRelay.Security.Tests;

[TestClass]
public class SpfEvaluatorTests
{
    private static readonly IPAddress Relay = IPAddress.Parse("85.31.156.82");

    private sealed class FakeResolver : ISpfDnsResolver
    {
        public Dictionary<string, string> Spf { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IPAddress[]> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> Mx { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetSpfRecordAsync(string domain, CancellationToken ct) =>
            Task.FromResult(Spf.TryGetValue(domain, out var r) ? r : null);

        public Task<IReadOnlyList<IPAddress>> GetAddressesAsync(string host, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(Hosts.TryGetValue(host, out var a) ? a : []);

        public Task<IReadOnlyList<string>> GetMxHostsAsync(string domain, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Mx.TryGetValue(domain, out var m) ? m : []);
    }

    private static Task<SpfResult> Evaluate(FakeResolver r, string domain = "example.li", IPAddress? ip = null) =>
        SpfEvaluator.EvaluateAsync(domain, ip ?? Relay, r, default);

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Ip4Listed_WithoutPublicHostname_Passes()
    {
        // The real-world case: the record authorises the relay's IP but has no "a:<public hostname>".
        var r = new FakeResolver();
        r.Spf["example.li"] = "v=spf1 a mx ip4:85.31.156.82 include:spf.protection.outlook.com ~all";
        r.Hosts["example.li"] = [IPAddress.Parse("203.0.113.5")];
        r.Mx["example.li"] = ["mail.example.li"];
        r.Hosts["mail.example.li"] = [IPAddress.Parse("203.0.113.6")];

        Assert.AreEqual(SpfResult.Pass, await Evaluate(r));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task IpNotAuthorised_ReachesSoftFailAll()
    {
        var r = new FakeResolver();
        r.Spf["example.li"] = "v=spf1 ip4:198.51.100.1 ~all";

        Assert.AreEqual(SpfResult.SoftFail, await Evaluate(r));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task HardFailAll_IsFail()
    {
        var r = new FakeResolver();
        r.Spf["example.li"] = "v=spf1 ip4:198.51.100.1 -all";

        Assert.AreEqual(SpfResult.Fail, await Evaluate(r));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Ip4Cidr_MatchesAddressInRange()
    {
        var r = new FakeResolver();
        r.Spf["example.li"] = "v=spf1 ip4:85.31.156.0/24 -all";

        Assert.AreEqual(SpfResult.Pass, await Evaluate(r));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Include_PassesThroughNestedRecord()
    {
        var r = new FakeResolver();
        r.Spf["example.li"] = "v=spf1 include:_spf.relay.example -all";
        r.Spf["_spf.relay.example"] = "v=spf1 ip4:85.31.156.82 -all";

        Assert.AreEqual(SpfResult.Pass, await Evaluate(r));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Include_FailingNestedRecord_DoesNotMatch_AndContinues()
    {
        var r = new FakeResolver();
        r.Spf["example.li"] = "v=spf1 include:_spf.other.example ~all";
        r.Spf["_spf.other.example"] = "v=spf1 ip4:198.51.100.1 -all";

        Assert.AreEqual(SpfResult.SoftFail, await Evaluate(r));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task AMechanism_ResolvesPublicHostname()
    {
        var r = new FakeResolver();
        r.Spf["example.li"] = "v=spf1 a:smtp.example.com -all";
        r.Hosts["smtp.example.com"] = [Relay];

        Assert.AreEqual(SpfResult.Pass, await Evaluate(r));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Redirect_EvaluatesTargetRecord()
    {
        var r = new FakeResolver();
        r.Spf["example.li"] = "v=spf1 redirect=_spf.example.li";
        r.Spf["_spf.example.li"] = "v=spf1 ip4:85.31.156.82 -all";

        Assert.AreEqual(SpfResult.Pass, await Evaluate(r));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NoRecord_IsNone()
    {
        Assert.AreEqual(SpfResult.None, await Evaluate(new FakeResolver()));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task MoreThanTenLookups_IsPermError()
    {
        var r = new FakeResolver();
        r.Spf["example.li"] = "v=spf1 " + string.Join(' ', Enumerable.Range(1, 11).Select(i => $"a:h{i}.example.li")) + " ip4:85.31.156.82 -all";
        for (var i = 1; i <= 11; i++)
            r.Hosts[$"h{i}.example.li"] = [IPAddress.Parse("198.51.100.1")];

        Assert.AreEqual(SpfResult.PermError, await Evaluate(r));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task IncludeWithoutRecord_IsPermError()
    {
        var r = new FakeResolver();
        r.Spf["example.li"] = "v=spf1 include:missing.example -all";

        Assert.AreEqual(SpfResult.PermError, await Evaluate(r));
    }
}

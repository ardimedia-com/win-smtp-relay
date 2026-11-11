using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Delivery.Tests;

/// <summary>
/// Real SMTP sessions against <see cref="FakeSmtpServer"/>: per-destination failure isolation (H1), one transaction
/// per smart host, null return path, plaintext-AUTH refusal (M13), MX target refusal (M10), DNS verdicts (M11) and
/// undecryptable secrets (M12).
/// </summary>
[TestClass]
public class SmtpDeliveryServiceTests
{
    private const int Tenant = TenantDefaults.DefaultTenantId;

    private static SmtpDeliveryService CreateService(StubRuntimeConfigCache cache, IMxResolver? mx = null, DeliveryOptions? options = null) =>
        new(
            mx ?? new FixedMxResolver([], []),
            Options.Create(options ?? new DeliveryOptions { ConnectTimeoutSeconds = 5 }),
            cache,
            new DkimSigningService(Options.Create(new DkimOptions()), NullLogger<DkimSigningService>.Instance),
            new StubDkimDomainService(),
            new PublicSuffixService(),
            new FixedDnsSettings(),
            NullLogger<SmtpDeliveryService>.Instance);

    private static QueuedMessage Message(string recipients, string sender = "sender@tenant.test") => new()
    {
        Id = 1,
        TenantId = Tenant,
        MessageId = "<m1@test>",
        Sender = sender,
        Recipients = recipients,
        RawMessage = Encoding.ASCII.GetBytes(
            "From: Sender <sender@tenant.test>\r\nTo: someone@a.test\r\nSubject: Test\r\nMessage-ID: <m1@test>\r\n"
            + "Date: Wed, 11 Nov 2026 10:00:00 +0000\r\nMIME-Version: 1.0\r\nContent-Type: text/plain\r\n\r\nHello\r\n"),
    };

    private static SendConnector Connector(int port, string? username = null, string? password = null, bool allowPlaintextAuth = false) => new()
    {
        Name = $"fake-{port}",
        SmartHost = "127.0.0.1",
        SmartHostPort = port,
        Username = username,
        EncryptedPassword = password,
        AllowPlaintextAuth = allowPlaintextAuth,
        OpportunisticTls = true,
        ConnectTimeoutSeconds = 5,
    };

    private static DomainRoute Route(string pattern, SendConnector connector) =>
        new() { TenantId = Tenant, DomainPattern = pattern, SendConnector = connector };

    [TestMethod]
    [TestCategory("Integration")]
    public async Task FailureOnOneDomain_KeepsTheOtherDomainsSuccess()
    {
        // H1: domain a.test is delivered; b.test's destination refuses connections. The 250 for a.test must come
        // back (so it is recorded as delivered), and b.test must be a temporary per-recipient failure — no throw.
        await using var server = new FakeSmtpServer();
        var cache = new StubRuntimeConfigCache
        {
            DomainRoutes = [Route("a.test", Connector(server.Port)), Route("b.test", Connector(FakeSmtpServer.UnusedPort()))],
        };

        var results = await CreateService(cache).DeliverAsync(Message("x@a.test;y@b.test"));

        Assert.AreEqual(2, results.Count);
        var a = results.Single(r => r.Recipient == "x@a.test");
        var b = results.Single(r => r.Recipient == "y@b.test");
        Assert.IsTrue(a.Success, a.StatusMessage);
        Assert.IsTrue(b.StatusCode.StartsWith('4'), $"{b.StatusCode} {b.StatusMessage}");
        Assert.IsTrue(b.ViaSmartHost);
        Assert.AreEqual(1, server.Transactions.Count);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SmartHost_AllDomainsInOneTransaction()
    {
        await using var server = new FakeSmtpServer();
        var cache = new StubRuntimeConfigCache();
        cache.DefaultConnectors[Tenant] = Connector(server.Port);

        var results = await CreateService(cache).DeliverAsync(Message("x@a.test;y@b.test;z@c.test"));

        Assert.IsTrue(results.All(r => r.Success));
        Assert.AreEqual(1, server.Transactions.Count, "one envelope for every recipient that leaves through the same smart host");
        CollectionAssert.AreEquivalent(new[] { "x@a.test", "y@b.test", "z@c.test" }, server.Transactions.Single().Recipients.ToArray());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task AlreadyDeliveredAndFailedRecipients_AreSkipped()
    {
        await using var server = new FakeSmtpServer();
        var cache = new StubRuntimeConfigCache();
        cache.DefaultConnectors[Tenant] = Connector(server.Port);
        var message = Message("x@a.test;y@b.test;z@c.test");
        message.DeliveredRecipients = "x@a.test";
        message.FailedRecipients = "z@c.test";

        var results = await CreateService(cache).DeliverAsync(message);

        Assert.AreEqual("y@b.test", results.Single().Recipient);
        CollectionAssert.AreEqual(new[] { "y@b.test" }, server.Transactions.Single().Recipients.ToArray());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task NullSender_IsSentAsEmptyReturnPath()
    {
        await using var server = new FakeSmtpServer();
        var cache = new StubRuntimeConfigCache();
        cache.DefaultConnectors[Tenant] = Connector(server.Port);

        var results = await CreateService(cache).DeliverAsync(Message("x@a.test", sender: ""));

        Assert.IsTrue(results.Single().Success, results.Single().StatusMessage);
        Assert.AreEqual("", server.Transactions.Single().MailFrom);
        Assert.IsTrue(server.Commands.Any(c => c.StartsWith("MAIL FROM:<>", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SmartHostWithoutTls_RefusesToSendCredentials()
    {
        // M13: the fake offers AUTH but no STARTTLS — the password must not cross the wire in clear text.
        await using var server = new FakeSmtpServer(advertiseAuth: true);
        var cache = new StubRuntimeConfigCache();
        cache.DefaultConnectors[Tenant] = Connector(server.Port, "user", "secret");

        var result = (await CreateService(cache).DeliverAsync(Message("x@a.test"))).Single();

        Assert.AreEqual("451", result.StatusCode);
        StringAssert.Contains(result.StatusMessage, "4.7.0");
        Assert.IsFalse(server.Commands.Any(c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(0, server.Transactions.Count);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SmartHostWithoutTls_SendsCredentials_WhenExplicitlyAllowed()
    {
        await using var server = new FakeSmtpServer(advertiseAuth: true);
        var cache = new StubRuntimeConfigCache();
        cache.DefaultConnectors[Tenant] = Connector(server.Port, "user", "secret", allowPlaintextAuth: true);

        var result = (await CreateService(cache).DeliverAsync(Message("x@a.test"))).Single();

        Assert.IsTrue(result.Success, result.StatusMessage);
        Assert.IsTrue(server.Commands.Any(c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UndecryptableSmartHostPassword_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI only applies on Windows.");
            return;
        }

        // M12: ciphertext the converter could not decrypt must never be sent as the password.
        await using var server = new FakeSmtpServer(advertiseAuth: true);
        var cache = new StubRuntimeConfigCache();
        cache.DefaultConnectors[Tenant] = Connector(server.Port, "user", "dpapi:AAAAAAAA", allowPlaintextAuth: true);

        var result = (await CreateService(cache).DeliverAsync(Message("x@a.test"))).Single();

        Assert.AreEqual("451", result.StatusCode);
        StringAssert.Contains(result.StatusMessage, "4.7.5");
        Assert.AreEqual(0, server.Commands.Count, "nothing is sent to the smart host");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MxOnLoopback_IsRefusedPermanently()
    {
        // M10: an MX that resolves to loopback is a loop — permanent 5.4.6, no connection.
        var mx = new FixedMxResolver(["mx.a.test"], [IPAddress.Loopback]);
        var result = (await CreateService(new StubRuntimeConfigCache(), mx).DeliverAsync(Message("x@a.test"))).Single();

        Assert.AreEqual("554", result.StatusCode);
        StringAssert.Contains(result.StatusMessage, "5.4.6");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MxOnPrivateNetwork_IsRefusedPermanently()
    {
        var mx = new FixedMxResolver(["mx.a.test"], [IPAddress.Parse("10.1.2.3")]);
        var result = (await CreateService(new StubRuntimeConfigCache(), mx).DeliverAsync(Message("x@a.test"))).Single();

        Assert.AreEqual("554", result.StatusCode);
        StringAssert.Contains(result.StatusMessage, "5.4.4");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DirectMx_DeliversAndRecordsTlsState_WhenPrivateTargetsAllowed()
    {
        await using var server = new FakeSmtpServer();
        var mx = new FixedMxResolver(["127.0.0.1"], [IPAddress.Loopback]);
        var service = CreateService(new StubRuntimeConfigCache(), mx,
            new DeliveryOptions { ConnectTimeoutSeconds = 5, AllowPrivateMxTargets = true });
        service.MxPort = server.Port;

        var result = (await service.DeliverAsync(Message("x@a.test"))).Single();

        Assert.IsTrue(result.Success, result.StatusMessage);
        StringAssert.Contains(result.StatusMessage, "no TLS");
        Assert.IsFalse(result.ViaSmartHost);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task DomainWithoutMail_IsPermanent_DnsOutageIsTemporary()
    {
        var permanent = new FixedMxResolver([], []) { Throw = new MxLookupException("Domain a.test does not exist (NXDOMAIN)", isPermanent: true) };
        var r1 = (await CreateService(new StubRuntimeConfigCache(), permanent).DeliverAsync(Message("x@a.test"))).Single();
        Assert.AreEqual("550", r1.StatusCode);
        StringAssert.Contains(r1.StatusMessage, "5.1.2");

        var temporary = new FixedMxResolver([], []) { Throw = new MxLookupException("SERVFAIL", isPermanent: false) };
        var r2 = (await CreateService(new StubRuntimeConfigCache(), temporary).DeliverAsync(Message("x@a.test"))).Single();
        Assert.AreEqual("451", r2.StatusCode);
        StringAssert.Contains(r2.StatusMessage, "4.4.3");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ClassifyMxTarget_SeparatesLoopsFromPrivateAndPublic()
    {
        IReadOnlyCollection<IPAddress> own = [IPAddress.Parse("203.0.113.7")];
        Assert.AreEqual(MxTargetRefusal.Loop, SmtpDeliveryService.ClassifyMxTarget([IPAddress.Loopback], own));
        Assert.AreEqual(MxTargetRefusal.Loop, SmtpDeliveryService.ClassifyMxTarget([IPAddress.Parse("203.0.113.7")], own));
        Assert.AreEqual(MxTargetRefusal.Loop, SmtpDeliveryService.ClassifyMxTarget([IPAddress.Any], own));
        Assert.AreEqual(MxTargetRefusal.PrivateNetwork, SmtpDeliveryService.ClassifyMxTarget([IPAddress.Parse("192.168.1.10")], own));
        Assert.AreEqual(MxTargetRefusal.PrivateNetwork, SmtpDeliveryService.ClassifyMxTarget([IPAddress.Parse("169.254.0.5")], own));
        Assert.AreEqual(MxTargetRefusal.PrivateNetwork, SmtpDeliveryService.ClassifyMxTarget([IPAddress.Parse("fe80::1")], own));
        Assert.IsNull(SmtpDeliveryService.ClassifyMxTarget([IPAddress.Parse("198.51.100.20")], own));
        Assert.IsNull(SmtpDeliveryService.ClassifyMxTarget([], own));
    }

    private sealed class FixedMxResolver(IReadOnlyList<string> hosts, IReadOnlyList<IPAddress> addresses) : IMxResolver
    {
        public MxLookupException? Throw { get; init; }

        public Task<IReadOnlyList<string>> ResolveMxAsync(string domain, CancellationToken cancellationToken = default) =>
            Throw is not null ? Task.FromException<IReadOnlyList<string>>(Throw) : Task.FromResult(hosts);

        public Task<IReadOnlyList<IPAddress>> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(addresses);
    }

    private sealed class FixedDnsSettings : IDnsSettingsService
    {
        public Task<DnsSettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new DnsSettings { PublicHostname = "relay.example.com" });
        public Task UpdateAsync(DnsSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }
}

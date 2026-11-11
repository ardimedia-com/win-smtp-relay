using System.Buffers;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WinSmtpRelay.Core.Configuration;

namespace WinSmtpRelay.SmtpListener.Tests;

/// <summary>Unit tests for the pieces behind the session guards and the content gates.</summary>
[TestClass]
public class SessionGuardUnitTests
{
    // ---- ConnectionLimiter ----

    [TestMethod]
    [TestCategory("Unit")]
    public void ConnectionLimiter_EnforcesPerIpAndGlobalCaps()
    {
        var limiter = new ConnectionLimiter(maxConnections: 3, maxConnectionsPerIp: 2);
        var a = IPAddress.Parse("192.0.2.1");
        var b = IPAddress.Parse("192.0.2.2");

        Assert.IsTrue(limiter.TryAcquire(a));
        Assert.IsTrue(limiter.TryAcquire(a));
        Assert.IsFalse(limiter.TryAcquire(a), "per-IP cap");
        Assert.IsTrue(limiter.TryAcquire(b));
        Assert.IsFalse(limiter.TryAcquire(IPAddress.Parse("192.0.2.3")), "global cap");

        limiter.Release(a);
        Assert.IsTrue(limiter.TryAcquire(IPAddress.Parse("192.0.2.3")));
        Assert.AreEqual(1, limiter.ActiveConnectionsFrom(a));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ConnectionLimiter_TreatsIpv4MappedAddressesAsTheSameClient()
    {
        var limiter = new ConnectionLimiter(0, 1);
        Assert.IsTrue(limiter.TryAcquire(IPAddress.Parse("192.0.2.1")));
        Assert.IsFalse(limiter.TryAcquire(IPAddress.Parse("::ffff:192.0.2.1")));
    }

    // ---- RawHeaderBlock ----

    private static RawHeaderBlock Read(string message) =>
        RawHeaderBlock.Read(new ReadOnlySequence<byte>(Encoding.Latin1.GetBytes(message)));

    [TestMethod]
    [TestCategory("Unit")]
    public void RawHeaderBlock_FindsFieldsWithFoldingAndStopsAtTheBody()
    {
        var block = Read("Received: from a\r\n\tby b; date\r\nFrom: <x@y>\r\nSubject: s\r\n\r\nFrom: <body@line>\r\n");

        Assert.AreEqual(3, block.Fields.Count);
        Assert.AreEqual(1, block.Count("from"), "a From line in the body is not a header");
        Assert.AreEqual("from a\tby b; date", block.First("Received")!.Value.Value);
        Assert.AreEqual("Received: from a\r\n\tby b; date\r\nFrom: <x@y>\r\nSubject: s\r\n".Length, block.Length);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RawHeaderBlock_MessageWithoutBody_IsAllHeaders()
    {
        var block = Read("From: <x@y>\r\nSubject: s\r\n");
        Assert.AreEqual(2, block.Fields.Count);
    }

    // ---- Message-ID ----

    [TestMethod]
    [TestCategory("Unit")]
    public void ExtractMessageId_FromRawHeaders_StripsLineBreaksAndControlCharacters()
    {
        var block = Read("Message-ID:\r\n <abc\tdef@example.com>\r\n\r\nbody");
        Assert.AreEqual("<abcdef@example.com>", RelayMessageStore.ExtractMessageId(null, block));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ExtractMessageId_NoHeader_IsNull_EvenWithOneInTheBody()
    {
        var block = Read("From: <x@y>\r\n\r\nMessage-ID: <evil@body>\r\n");
        Assert.IsNull(RelayMessageStore.ExtractMessageId(null, block));
    }

    // ---- Authentication-Results authserv-id ----

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("winsmtprelay; spf=pass", true)]
    [DataRow("  WinSmtpRelay 1; spf=pass", true)]
    [DataRow("winsmtprelay", true)]
    [DataRow("winsmtprelay.example.com; spf=pass", false)]
    [DataRow("mx.google.com; spf=pass", false)]
    public void ClaimsOurAuthServId(string value, bool expected) =>
        Assert.AreEqual(expected, RelayMessageStore.ClaimsOurAuthServId(value));

    // ---- Internal addresses ----

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("127.0.0.1", true)]
    [DataRow("10.1.2.3", true)]
    [DataRow("172.20.0.1", true)]
    [DataRow("192.168.1.1", true)]
    [DataRow("100.64.0.1", true)]
    [DataRow("fd00::1", true)]
    [DataRow("fe80::1", true)]
    [DataRow("203.0.113.5", false)]
    [DataRow("2001:db8::1", false)]
    public void IsInternalAddress(string address, bool expected) =>
        Assert.AreEqual(expected, RelayMessageStore.IsInternalAddress(IPAddress.Parse(address)));

    // ---- HELO sanitising ----

    [TestMethod]
    [TestCategory("Unit")]
    public void SanitizeDomain_DropsEverythingAHeaderCouldBeInjectedWith()
    {
        Assert.AreEqual("hostexample.comX-Evil:yes", RelayMessageStore.SanitizeDomain("host example.com\r\nX-Evil: yes"));
        Assert.AreEqual("unknown", RelayMessageStore.SanitizeDomain("\r\n"));
    }

    // ---- RejectionBuffer ----

    [TestMethod]
    [TestCategory("Unit")]
    public void RejectionBuffer_LineAfterAuth_IsNeverStored()
    {
        Assert.AreEqual(RejectionBuffer.AuthContinuationMarker,
            RejectionBuffer.Redact(Encoding.ASCII.GetBytes("dXNlckBleGFtcGxlLmNvbQ=="), followsAuth: true));
    }

    // ---- Endpoint validation ----

    [TestMethod]
    [TestCategory("Unit")]
    public void ValidateEndpoints_DropsEndpointsThatCanNeverWork()
    {
        var endpoints = new List<EndpointOptions>
        {
            new() { Port = 25 },
            new() { Port = 587, RequireAuth = true },                     // AUTH is never offered without TLS
            new() { Port = 465, ImplicitTls = true, RequireAuth = true }, // needs a certificate
            new() { Port = 2525, RequireTls = true }                      // needs a certificate
        };

        var withoutCert = SmtpRelayServer.ValidateEndpoints(endpoints, hasCertificate: false, NullLogger.Instance);
        CollectionAssert.AreEqual(new[] { 25 }, withoutCert.Select(e => e.Port).ToList());

        var withCert = SmtpRelayServer.ValidateEndpoints(endpoints, hasCertificate: true, NullLogger.Instance);
        CollectionAssert.AreEqual(new[] { 25, 465, 2525 }, withCert.Select(e => e.Port).ToList());
    }
}

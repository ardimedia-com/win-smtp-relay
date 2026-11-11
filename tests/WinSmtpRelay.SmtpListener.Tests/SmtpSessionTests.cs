using System.Text;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.SmtpListener.Tests;

/// <summary>
/// Whole SMTP sessions against the real listener (see <see cref="RelayHarness"/>): what a client on the wire
/// is answered, what is queued, and which gate recorded a refusal. The review of 2026-11-11 found the
/// session-level defects (null sender, tenant per message, size on DATA, TLS enforcement) precisely because
/// there were no tests at this level.
/// </summary>
[TestClass]
public class SmtpSessionTests
{
    private const string Hosted = "hosted.example";
    private const string External = "external.example";

    private static StubRuntimeConfigCache HostedCache(params IpAccessRule[] rules) => new()
    {
        AcceptedDomains = [Hosted],
        IpAccessRules = [.. rules]
    };

    private static IpAccessRule AllowLoopback(int tenantId = TenantDefaults.DefaultTenantId) =>
        new() { TenantId = tenantId, Network = "127.0.0.0/8", Action = IpAccessAction.Allow, SortOrder = 0 };

    private static string Body(string from = "Sender <a@a.example>", string extraHeaders = "", string body = "Hello") =>
        $"From: {from}\r\nTo: <x@{Hosted}>\r\nSubject: test\r\nMessage-ID: <m{Guid.NewGuid():N}@a.example>\r\n{extraHeaders}\r\n{body}\r\n";

    private static void Send(SmtpTestClient c, string mailFrom, string rcpt, string? body = null, string expectFinal = "250")
    {
        StringAssert.StartsWith(c.Send($"MAIL FROM:<{mailFrom}>"), "250");
        StringAssert.StartsWith(c.Send($"RCPT TO:<{rcpt}>"), "250");
        StringAssert.StartsWith(c.Send("DATA"), "354");
        StringAssert.StartsWith(c.SendBody(body ?? Body()), expectFinal);
    }

    // ---- H7: null sender ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task NullSender_ToHostedDomain_IsAccepted()
    {
        await using var relay = await RelayHarness.StartAsync(HostedCache());
        using var c = await relay.ConnectAsync();
        c.Send("EHLO bounce.example");

        StringAssert.StartsWith(c.Send("MAIL FROM:<>"), "250");
        StringAssert.StartsWith(c.Send($"RCPT TO:<postmaster@{Hosted}>"), "250");
        StringAssert.StartsWith(c.Send("DATA"), "354");
        StringAssert.StartsWith(c.SendBody(Body(from: "Mail Delivery System <MAILER-DAEMON@bounce.example>")), "250");

        Assert.IsTrue(relay.Queue.Messages.TryPeek(out var queued));
        Assert.AreEqual("", queued.Sender);
        Assert.AreEqual($"postmaster@{Hosted}", queued.Recipients);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task NullSender_ToExternalDomain_IsRefused_EvenFromARelayingNetwork()
    {
        // The loopback client may relay (explicit allow rule), but a bounce never leaves for a third party.
        await using var relay = await RelayHarness.StartAsync(HostedCache(AllowLoopback()));
        using var c = await relay.ConnectAsync();
        c.Send("EHLO bounce.example");

        StringAssert.StartsWith(c.Send("MAIL FROM:<>"), "250");
        StringAssert.StartsWith(c.Send($"RCPT TO:<victim@{External}>"), "550");
        Assert.IsTrue(relay.Recorder.Recorded.Any(r => r.Reason == RejectReason.NullSenderRelayDenied));

        // The session survives: a normal message still goes through.
        StringAssert.StartsWith(c.Send("RSET"), "250");
        Send(c, "a@a.example", $"someone@{External}");
    }

    // ---- H8: tenant per message ----

    private static StubRuntimeConfigCache TwoTenantCache()
    {
        var cache = HostedCache(AllowLoopback());
        cache.SenderDomainOwners["a.example"] = 2;
        cache.SenderDomainOwners["b.example"] = 3;
        return cache;
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SecondMessageInSession_IsAttributedToItsOwnTenant_AfterRset()
    {
        await using var relay = await RelayHarness.StartAsync(TwoTenantCache());
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        Send(c, "a@a.example", $"x@{Hosted}");
        StringAssert.StartsWith(c.Send("RSET"), "250");
        Send(c, "b@b.example", $"x@{Hosted}", Body(from: "<b@b.example>"));

        var tenants = relay.Queue.Messages.Select(m => m.TenantId).ToList();
        CollectionAssert.AreEqual(new[] { 2, 3 }, tenants);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SecondMessageInSession_IsAttributedToItsOwnTenant_WithoutRset()
    {
        await using var relay = await RelayHarness.StartAsync(TwoTenantCache());
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        Send(c, "a@a.example", $"x@{Hosted}");
        Send(c, "b@b.example", $"x@{Hosted}", Body(from: "<b@b.example>"));

        CollectionAssert.AreEqual(new[] { 2, 3 }, relay.Queue.Messages.Select(m => m.TenantId).ToList());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SecondMessageInSession_ForADisabledTenant_IsRefused()
    {
        var cache = TwoTenantCache();
        cache.DisabledTenants.Add(3);
        await using var relay = await RelayHarness.StartAsync(cache);
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        Send(c, "a@a.example", $"x@{Hosted}");
        StringAssert.StartsWith(c.Send("MAIL FROM:<b@b.example>"), "550");

        Assert.AreEqual(1, relay.Queue.Messages.Count);
        Assert.IsTrue(relay.Recorder.Recorded.Any(r => r.Reason == RejectReason.TenantDisabled && r.TenantId == 3));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SecondMessageInSession_IsJudgedByItsOwnTenantsDenyRule()
    {
        // Tenant 3 denies the loopback address; tenant 2 has no rule of its own (host baseline allows it).
        var cache = TwoTenantCache();
        cache.IpAccessRules.Add(new IpAccessRule { TenantId = 3, Network = "127.0.0.1/32", Action = IpAccessAction.Deny, SortOrder = 0 });
        await using var relay = await RelayHarness.StartAsync(cache);
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        Send(c, "a@a.example", $"x@{Hosted}");
        StringAssert.StartsWith(c.Send("MAIL FROM:<b@b.example>"), "550");

        Assert.IsTrue(relay.Recorder.Recorded.Any(r => r.Reason == RejectReason.IpBlocked && r.TenantId == 3));
    }

    // ---- H9: size on DATA ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task OversizeData_WithoutSizeParameter_Is552_AndTheSessionContinues()
    {
        await using var relay = await RelayHarness.StartAsync(HostedCache(), o => o.MaxMessageSizeBytes = 1000);
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        StringAssert.StartsWith(c.Send("MAIL FROM:<a@a.example>"), "250");
        StringAssert.StartsWith(c.Send($"RCPT TO:<x@{Hosted}>"), "250");
        StringAssert.StartsWith(c.Send("DATA"), "354");
        StringAssert.StartsWith(c.SendBody(Body(body: new string('x', 5000))), "552");

        Assert.AreEqual(0, relay.Queue.Messages.Count);
        Assert.IsTrue(relay.Recorder.Recorded.Any(r => r.Reason == RejectReason.MessageTooLarge));
        StringAssert.StartsWith(c.Send("NOOP"), "250");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RunawayData_IsStoppedWith552_AndTheSessionIsClosed()
    {
        // A body that keeps coming without an end marker must not grow memory without bound. The client
        // sends exactly one byte more than the receive guard allows, then waits for the answer.
        const int limit = 1000;
        await using var relay = await RelayHarness.StartAsync(HostedCache(), o => o.MaxMessageSizeBytes = limit);
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        StringAssert.StartsWith(c.Send("MAIL FROM:<a@a.example>"), "250");
        StringAssert.StartsWith(c.Send($"RCPT TO:<x@{Hosted}>"), "250");
        StringAssert.StartsWith(c.Send("DATA"), "354");

        c.WriteRaw(Enumerable.Repeat((byte)'x', limit + SmtpRelayServer.DataBufferSlackBytes + 1).ToArray());

        StringAssert.StartsWith(c.ReadReply(), "552");
        Assert.IsTrue(c.IsClosedByServer(), "the rest of the body must not be read as commands");
        Assert.AreEqual(0, relay.Queue.Messages.Count);
    }

    // ---- M1: TLS required ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RequireTls_MailBeforeStartTls_Is530_AndWorksAfterIt()
    {
        await using var relay = await RelayHarness.StartAsync(
            HostedCache(),
            endpoint: port => new EndpointOptions { Address = "127.0.0.1", Port = port, RequireTls = true },
            withCertificate: true);
        using var c = await relay.ConnectAsync();

        var ehlo = c.SendMultiline("EHLO client.example");
        Assert.IsTrue(ehlo.Any(l => l.Contains("STARTTLS", StringComparison.Ordinal)));

        StringAssert.StartsWith(c.Send("MAIL FROM:<a@a.example>"), "530");
        Assert.IsTrue(relay.Recorder.Recorded.Any(r => r.Reason == RejectReason.TlsRequired));

        StringAssert.StartsWith(c.StartTls(), "220");
        c.Send("EHLO client.example");
        Send(c, "a@a.example", $"x@{Hosted}");

        Assert.IsTrue(relay.Queue.Messages.TryPeek(out var queued));
        StringAssert.Contains(Encoding.ASCII.GetString(queued.RawMessage), "with ESMTPS");
    }

    // ---- M6: recipients and connections ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RecipientCap_ExcessRecipientIs452_AndTheMessageKeepsTheAcceptedOnes()
    {
        await using var relay = await RelayHarness.StartAsync(HostedCache(), o => o.MaxRecipientsPerMessage = 2);
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        StringAssert.StartsWith(c.Send("MAIL FROM:<a@a.example>"), "250");
        StringAssert.StartsWith(c.Send($"RCPT TO:<one@{Hosted}>"), "250");
        StringAssert.StartsWith(c.Send($"RCPT TO:<two@{Hosted}>"), "250");
        StringAssert.StartsWith(c.Send($"RCPT TO:<three@{Hosted}>"), "452");
        StringAssert.StartsWith(c.Send("DATA"), "354");
        StringAssert.StartsWith(c.SendBody(Body()), "250");

        Assert.IsTrue(relay.Queue.Messages.TryPeek(out var queued));
        Assert.AreEqual($"one@{Hosted};two@{Hosted}", queued.Recipients);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SenderBudget_CountsRecipients()
    {
        var cache = HostedCache();
        cache.RateLimitSettings = new RateLimitSettings { MaxMessagesPerSenderPerMinute = 3, MaxMessagesPerSenderPerDay = 1000, MaxConnectionsPerIpPerMinute = 0 };
        await using var relay = await RelayHarness.StartAsync(cache);
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        // One message to three recipients spends the per-minute budget ...
        StringAssert.StartsWith(c.Send("MAIL FROM:<a@a.example>"), "250");
        for (var i = 0; i < 3; i++)
            StringAssert.StartsWith(c.Send($"RCPT TO:<r{i}@{Hosted}>"), "250");
        StringAssert.StartsWith(c.Send("DATA"), "354");
        StringAssert.StartsWith(c.SendBody(Body()), "250");

        // ... so the next message is throttled (451, temporary).
        StringAssert.StartsWith(c.Send("MAIL FROM:<a@a.example>"), "451");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PerIpConnectionCap_RefusesTheExtraConnectionWith421()
    {
        await using var relay = await RelayHarness.StartAsync(HostedCache(), o => o.MaxConnectionsPerIp = 1);

        // The harness's readiness probe may still hold the slot for a moment.
        var first = await relay.ConnectAsync();
        for (var i = 0; i < 50 && first.Banner?.StartsWith("220", StringComparison.Ordinal) != true; i++)
        {
            first.Dispose();
            await Task.Delay(50);
            first = await relay.ConnectAsync();
        }

        using (first)
        {
            StringAssert.StartsWith(first.Banner, "220");

            using var second = await relay.ConnectAsync();
            StringAssert.StartsWith(second.Banner, "421");
            Assert.IsTrue(second.IsClosedByServer());

            StringAssert.StartsWith(first.Send("NOOP"), "250");
            first.Send("QUIT");
        }

        Assert.IsTrue(relay.Recorder.Recorded.Any(r => r.Reason == RejectReason.TooManyConnections));

        // The slot is returned when the session ends.
        for (var i = 0; i < 50; i++)
        {
            using var again = await relay.ConnectAsync();
            if (again.Banner?.StartsWith("220", StringComparison.Ordinal) == true)
                return;
            await Task.Delay(50);
        }

        Assert.Fail("the connection slot was never released");
    }

    // ---- M2 / M4 / LOW: content gates and trace headers ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task HeaderFrom_OutsideTheAcceptedSenderDomains_IsRefused()
    {
        var cache = HostedCache();
        cache.AcceptedSenderDomains = ["good.example"];
        await using var relay = await RelayHarness.StartAsync(cache);
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        // The envelope passes the gate; the From the recipient would see (and DKIM would sign) does not.
        Send(c, "billing@good.example", $"x@{Hosted}", Body(from: "CEO <ceo@evil.example>"), expectFinal: "550");

        Assert.AreEqual(0, relay.Queue.Messages.Count);
        Assert.IsTrue(relay.Recorder.Recorded.Any(r => r.Reason == RejectReason.SenderDomainNotAccepted && r.Detail == "header From"));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task TwoFromHeaders_AreRefused()
    {
        await using var relay = await RelayHarness.StartAsync(HostedCache());
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        Send(c, "a@a.example", $"x@{Hosted}", Body(extraHeaders: "From: <other@evil.example>\r\n"), expectFinal: "550");
        Assert.IsTrue(relay.Recorder.Recorded.Any(r => r.Reason == RejectReason.MultipleFromHeaders));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task TooManyReceivedHeaders_IsRefusedAsALoop()
    {
        await using var relay = await RelayHarness.StartAsync(HostedCache(), o => o.MaxReceivedHeaders = 5);
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        var hops = string.Concat(Enumerable.Range(1, 6).Select(i => $"Received: from hop{i}.example by relay.example; Mon, 9 Nov 2026 10:00:00 +0000\r\n"));
        Send(c, "a@a.example", $"x@{Hosted}", hops + Body(), expectFinal: "554");
        Assert.IsTrue(relay.Recorder.Recorded.Any(r => r.Reason == RejectReason.HopLimitExceeded));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task QueuedMessage_GetsAReceivedHeader_AndLosesAForgedAuthenticationResults()
    {
        await using var relay = await RelayHarness.StartAsync(HostedCache());
        using var c = await relay.ConnectAsync();
        c.Send("EHLO printer-3f.corp.local");

        var forged = "Authentication-Results: winsmtprelay;\r\n\tspf=pass; dmarc=pass\r\nAuthentication-Results: mx.other.example; spf=pass\r\n";
        Send(c, "a@a.example", $"x@{Hosted}", forged + Body());

        Assert.IsTrue(relay.Queue.Messages.TryPeek(out var queued));
        var raw = Encoding.ASCII.GetString(queued.RawMessage);

        StringAssert.StartsWith(raw, "Received: by WinSmtpRelay (WinSmtpRelay) with ESMTP;");
        Assert.IsFalse(raw.Contains("printer-3f", StringComparison.Ordinal), "an internal client's HELO name stays inside");
        Assert.IsFalse(raw.Contains("Authentication-Results: winsmtprelay", StringComparison.Ordinal), "a forged verdict under our authserv-id is removed");
        StringAssert.Contains(raw, "Authentication-Results: mx.other.example; spf=pass", "other relays' headers are kept");
        StringAssert.Contains(raw, "\r\n\r\nHello", "the body is untouched");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MessageId_IsTakenFromTheHeaders_NeverFromTheBody()
    {
        await using var relay = await RelayHarness.StartAsync(HostedCache());
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        var noIdHeaders = "From: <a@a.example>\r\nSubject: quoted\r\n\r\nForwarded:\r\nMessage-ID: <evil@body.example>\r\n";
        Send(c, "a@a.example", $"x@{Hosted}", noIdHeaders);

        Assert.IsTrue(relay.Queue.Messages.TryPeek(out var queued));
        Assert.AreNotEqual("<evil@body.example>", queued.MessageId);
        StringAssert.EndsWith(queued.MessageId, "@winsmtprelay>");
    }

    // ---- LOW: AUTH continuation lines never reach the rejection buffer ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RefusedAuth_ContinuationLinesAreNotStored()
    {
        // AUTH is not offered in plain text, so the server refuses the AUTH command itself; a client that
        // carries on regardless sends its base64 user name and password as the next "commands".
        await using var relay = await RelayHarness.StartAsync(HostedCache());
        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");

        var auth = c.Send("AUTH LOGIN");
        Assert.IsFalse(auth!.StartsWith("334", StringComparison.Ordinal), "AUTH must not run over plain text");
        c.Send("dXNlckBleGFtcGxlLmNvbQ==");   // user@example.com
        c.Send("czNjcjN0LXBhc3N3MHJk");       // s3cr3t-passw0rd
        c.Send("GARBAGE-LINE");               // an ordinary garbage line after the window

        var buffers = relay.Recorder.Recorded.Select(r => r.RawBuffer).Where(b => b is not null).ToList();
        Assert.IsFalse(buffers.Any(b => b!.Contains("dXNlck", StringComparison.Ordinal) || b.Contains("czNjcjN0", StringComparison.Ordinal)),
            "a credential reached the rejection record: " + string.Join(" | ", buffers));
        Assert.AreEqual(2, buffers.Count(b => b == RejectionBuffer.AuthContinuationMarker));
        Assert.IsTrue(buffers.Contains("GARBAGE-LINE"), "lines outside the AUTH window keep their diagnostic value");
    }

    // ---- H8 / M2 for authenticated sessions ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task AuthenticatedSession_KeepsItsTenant_AndHeaderFromIsBoundBySendAs()
    {
        var cache = TwoTenantCache();
        await using var relay = await RelayHarness.StartAsync(
            cache,
            endpoint: port => new EndpointOptions { Address = "127.0.0.1", Port = port, RequireTls = true, RequireAuth = true },
            withCertificate: true);
        relay.Users.Users.Add((new RelayUser { Id = 7, TenantId = 5, Username = "alice", PasswordHash = "-", AllowedSenderAddresses = "a@a.example;b@b.example" }, "pw"));

        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");
        StringAssert.StartsWith(c.StartTls(), "220");
        c.Send("EHLO client.example");
        StringAssert.StartsWith(c.Send("AUTH PLAIN " + Convert.ToBase64String(Encoding.ASCII.GetBytes("\0alice\0pw"))), "235");

        // Sender domains owned by tenants 2 and 3: an authenticated message still belongs to the user's tenant.
        Send(c, "a@a.example", $"x@{External}");
        StringAssert.StartsWith(c.Send("RSET"), "250");
        Send(c, "b@b.example", $"x@{External}", Body(from: "<b@b.example>"));
        CollectionAssert.AreEqual(new[] { 5, 5 }, relay.Queue.Messages.Select(m => m.TenantId).ToList());

        // SendAs now also binds the header From.
        Send(c, "a@a.example", $"x@{External}", Body(from: "Boss <boss@a.example>"), expectFinal: "550");
        Assert.IsTrue(relay.Recorder.Recorded.Any(r => r.Reason == RejectReason.SendAsDenied));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task FailedAuth_LeavesTheSessionUnauthenticated()
    {
        await using var relay = await RelayHarness.StartAsync(
            HostedCache(),
            endpoint: port => new EndpointOptions { Address = "127.0.0.1", Port = port, RequireTls = true },
            withCertificate: true);
        relay.Users.Users.Add((new RelayUser { Id = 7, TenantId = 5, Username = "alice", PasswordHash = "-" }, "pw"));

        using var c = await relay.ConnectAsync();
        c.Send("EHLO client.example");
        c.StartTls();
        c.Send("EHLO client.example");
        StringAssert.StartsWith(c.Send("AUTH PLAIN " + Convert.ToBase64String(Encoding.ASCII.GetBytes("\0alice\0wrong"))), "535");

        // Not authenticated: relaying to an external domain is refused (open-relay protection).
        StringAssert.StartsWith(c.Send("MAIL FROM:<a@a.example>"), "250");
        StringAssert.StartsWith(c.Send($"RCPT TO:<x@{External}>"), "550");
    }
}

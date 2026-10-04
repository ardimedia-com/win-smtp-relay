using System.Text;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Integration.Tests;

/// <summary>
/// The lenient MAIL FROM / RCPT TO syntax against a real listener: a bare address (no angle brackets,
/// optionally after a space) from a trusted network is accepted and queued; the same line from an
/// untrusted client is still refused with the library's 502 and recorded as before.
/// <para>
/// This runs over a real socket because the mechanism wraps the SMTP library's pipe — the claims that
/// matter (one rewritten line per read, pipelined commands behind it untouched, the DATA body passed
/// through byte for byte) are about the library's read loop, which only a real session exercises. The
/// reference floats on <c>11.*</c>; if a future version reads its input differently, these say so.
/// </para>
/// </summary>
[TestClass]
public class LenientAddressSyntaxEndToEndTests
{
    private const int ConformantTestPort = 9031;
    private const int BareAddressTestPort = 9032;
    private const int BracketAfterSpaceTestPort = 9033;
    private const int PipelinedParametersTestPort = 9034;
    private const int UntrustedTestPort = 9035;

    // Deliberately not example.com: its published DMARC p=reject would make inbound authentication, not
    // the syntax under test, decide the outcome.
    private const string Sender = "stodat03@winsmtprelay.local";
    private const string Recipient = "support@winsmtprelay.test";

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ConformantSyntax_IsStillAccepted()
    {
        await using var relay = await RelayUnderTest.StartAsync(ConformantTestPort, allowedNetworks: ["127.0.0.1/32"]);

        await relay.TalkAsync(session =>
        {
            session.Send("EHLO tester");
            StringAssert.StartsWith(session.Send($"MAIL FROM:<{Sender}>"), "250");
            StringAssert.StartsWith(session.Send($"RCPT TO:<{Recipient}>"), "250");
            StringAssert.StartsWith(session.Send("DATA"), "354");
            StringAssert.StartsWith(session.SendBody(Body("conformant")), "250");
        });

        var queued = await SingleQueuedAsync(relay);
        Assert.AreEqual(Sender, queued.Sender);
        StringAssert.Contains(queued.Recipients, Recipient);
    }

    /// <summary>
    /// The HP MSA2012 case: <c>MAIL FROM: stodat03@…</c> and <c>RCPT TO: support@…</c>. Also proves the
    /// DATA body is not rewritten — it contains a line that would match if the body were inspected.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task BareAddressAfterASpace_FromATrustedNetwork_IsAcceptedAndQueued()
    {
        await using var relay = await RelayUnderTest.StartAsync(BareAddressTestPort, allowedNetworks: ["127.0.0.1/32"]);

        const string bodyLineThatMustSurvive = "MAIL FROM: quoted@winsmtprelay.local";

        await relay.TalkAsync(session =>
        {
            session.Send("EHLO stodat03");
            StringAssert.StartsWith(session.Send($"MAIL FROM: {Sender}"), "250");
            StringAssert.StartsWith(session.Send($"RCPT TO: {Recipient}"), "250");
            StringAssert.StartsWith(session.Send("DATA"), "354");
            StringAssert.StartsWith(session.SendBody(Body(bodyLineThatMustSurvive)), "250");
        });

        var queued = await SingleQueuedAsync(relay);
        Assert.AreEqual(Sender, queued.Sender);
        StringAssert.Contains(queued.Recipients, Recipient);
        StringAssert.Contains(Encoding.ASCII.GetString(queued.RawMessage), bodyLineThatMustSurvive,
            "bytes read while DATA executes must reach the store unchanged");

        var rejections = await relay.QueryAsync(db => db.RejectedSubmissions.AsNoTracking().ToListAsync());
        Assert.AreEqual(0, rejections.Count, "an accepted submission records no rejection");
    }

    /// <summary>A space before an already-bracketed path: the library accepts it natively; the wrapper must not disturb that.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task SpaceBeforeABracketedAddress_IsAccepted()
    {
        await using var relay = await RelayUnderTest.StartAsync(BracketAfterSpaceTestPort, allowedNetworks: ["127.0.0.1/32"]);

        await relay.TalkAsync(session =>
        {
            session.Send("EHLO tester");
            StringAssert.StartsWith(session.Send($"MAIL FROM: <{Sender}>"), "250");
            StringAssert.StartsWith(session.Send($"RCPT TO: <{Recipient}>"), "250");
            StringAssert.StartsWith(session.Send("DATA"), "354");
            StringAssert.StartsWith(session.SendBody(Body("bracket after space")), "250");
        });

        Assert.AreEqual(Sender, (await SingleQueuedAsync(relay)).Sender);
    }

    /// <summary>
    /// ESMTP parameters after a bare address, with all three commands in one write — so the rewritten
    /// MAIL line and the RCPT line behind it share a buffer and the position mapping is exercised.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task BareAddressWithParameters_Pipelined_IsAccepted()
    {
        await using var relay = await RelayUnderTest.StartAsync(PipelinedParametersTestPort, allowedNetworks: ["127.0.0.1/32"]);

        await relay.TalkAsync(session =>
        {
            session.Send("EHLO tester");
            var replies = session.SendPipelined(
                $"MAIL FROM: {Sender} SIZE=1000 BODY=8BITMIME",
                $"RCPT TO:{Recipient}",
                "DATA");

            StringAssert.StartsWith(replies[0], "250", $"MAIL with parameters: {replies[0]}");
            StringAssert.StartsWith(replies[1], "250", $"RCPT without space or brackets: {replies[1]}");
            StringAssert.StartsWith(replies[2], "354", $"DATA: {replies[2]}");
            StringAssert.StartsWith(session.SendBody(Body("pipelined")), "250");
        });

        var queued = await SingleQueuedAsync(relay);
        Assert.AreEqual(Sender, queued.Sender);
        StringAssert.Contains(queued.Recipients, Recipient);
    }

    /// <summary>
    /// Leniency is for configured devices only. From anywhere else the line is refused exactly as before
    /// — the library's 502 — and recorded as a syntax rejection carrying the line the client really sent.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task BareAddress_FromAnUntrustedClient_IsStillRefusedWith502()
    {
        // 127.0.0.1 is deliberately outside every configured network.
        await using var relay = await RelayUnderTest.StartAsync(UntrustedTestPort, allowedNetworks: ["10.99.99.0/24"]);

        string? reply = null;
        await relay.TalkAsync(session =>
        {
            session.Send("EHLO tester");
            reply = session.Send($"MAIL FROM: {Sender}");
        });

        StringAssert.StartsWith(reply, "502", $"an untrusted client must stay strict, got: {reply}");

        var rows = await relay.StopAndReadRowsAsync();
        var syntax = rows.SingleOrDefault(r => r.Reason == RejectReason.CommandSyntaxError);
        Assert.IsNotNull(syntax, "the refused line is still recorded");
        Assert.AreEqual($"MAIL FROM: {Sender}", syntax.LastBuffer, "the record carries the original, un-normalised line");
        Assert.IsFalse(syntax.IsTrustedSource);
    }

    private static string Body(string text) =>
        $"From: {Sender}\r\nTo: {Recipient}\r\nSubject: lenient syntax test\r\n\r\n{text}";

    private static async Task<QueuedMessage> SingleQueuedAsync(RelayUnderTest relay)
    {
        var messages = await relay.QueryAsync(db => db.QueuedMessages.AsNoTracking().ToListAsync());
        Assert.AreEqual(1, messages.Count, "exactly one message is queued");
        return messages[0];
    }
}

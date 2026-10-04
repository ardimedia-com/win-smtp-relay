using WinSmtpRelay.SmtpListener;

namespace WinSmtpRelay.SmtpListener.Tests;

/// <summary>
/// The line rewrite behind the lenient MAIL FROM / RCPT TO syntax. The rule that matters most is the
/// negative one: a line that is already conformant — or is not an envelope command at all — must come
/// back untouched, because the caller passes every non-match to the strict parser as-is.
/// </summary>
[TestClass]
public class LenientAddressSyntaxTests
{
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("MAIL FROM: stodat03@ardimedia.com", "MAIL FROM:<stodat03@ardimedia.com>")]
    [DataRow("MAIL FROM:a@b.c", "MAIL FROM:<a@b.c>")]
    [DataRow("MAIL FROM:   a@b.c", "MAIL FROM:<a@b.c>")]
    [DataRow("MAIL FROM : a@b.c", "MAIL FROM:<a@b.c>")]
    [DataRow("mail from: a@b.c", "MAIL FROM:<a@b.c>")]
    [DataRow("MAIL  FROM:\ta@b.c", "MAIL FROM:<a@b.c>")]
    [DataRow("RCPT TO: support@bvd.li", "RCPT TO:<support@bvd.li>")]
    [DataRow("rcpt to:x@y.z", "RCPT TO:<x@y.z>")]
    public void TryNormalize_WrapsABareAddressInAngleBrackets(string line, string expected)
    {
        Assert.IsTrue(LenientAddressSyntax.TryNormalize(line, out var normalized));
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("MAIL FROM: a@b.c SIZE=1234", "MAIL FROM:<a@b.c> SIZE=1234")]
    [DataRow("MAIL FROM: a@b.c SIZE=1234 BODY=8BITMIME", "MAIL FROM:<a@b.c> SIZE=1234 BODY=8BITMIME")]
    [DataRow("MAIL FROM:a@b.c BODY=7BIT", "MAIL FROM:<a@b.c> BODY=7BIT")]
    [DataRow("RCPT TO: x@y.z NOTIFY=NEVER", "RCPT TO:<x@y.z> NOTIFY=NEVER")]
    public void TryNormalize_KeepsEsmtpParametersAfterTheAddress(string line, string expected)
    {
        Assert.IsTrue(LenientAddressSyntax.TryNormalize(line, out var normalized));
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("MAIL FROM:<a@b.c>")]            // conformant
    [DataRow("MAIL FROM: <a@b.c>")]           // space before a bracketed path — the library accepts it itself
    [DataRow("MAIL FROM:<a@b.c> SIZE=1234")]  // conformant with parameters
    [DataRow("MAIL FROM:<>")]                 // null reverse-path (bounces)
    [DataRow("RCPT TO:<x@y.z>")]
    [DataRow("RCPT TO: <x@y.z>")]
    public void TryNormalize_LeavesConformantLinesAlone(string line)
    {
        Assert.IsFalse(LenientAddressSyntax.TryNormalize(line, out var normalized));
        Assert.AreEqual(line, normalized, "a non-match must hand back the line unchanged");
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("EHLO stodat03")]
    [DataRow("DATA")]
    [DataRow("QUIT")]
    [DataRow("AUTH PLAIN dGVzdAB0ZXN0AHNlY3JldA==")]
    [DataRow("MAIL FROM:")]                    // no address at all
    [DataRow("MAIL FROM: postmaster")]         // no '@': not the quirk this exists for
    [DataRow("MAIL FROM: Device <a@b.c>")]     // display name: not a bare address
    [DataRow("MAIL FROM: a@b.c>")]             // half-bracketed: malformed, left for the parser to refuse
    [DataRow("MAIL a@b.c")]                    // no FROM:
    [DataRow("MAILFROM: a@b.c")]               // verb and keyword run together
    public void TryNormalize_IgnoresEverythingElse(string line)
    {
        Assert.IsFalse(LenientAddressSyntax.TryNormalize(line, out _));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TryNormalize_IgnoresAnOverlongLine()
    {
        var line = "MAIL FROM: " + new string('a', 2000) + "@b.c";

        Assert.IsFalse(LenientAddressSyntax.TryNormalize(line, out _), "far beyond RFC 5321's 512 octets is not a device quirk");
    }
}

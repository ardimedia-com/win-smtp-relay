using System.Text;
using WinSmtpRelay.SmtpListener;

namespace WinSmtpRelay.SmtpListener.Tests;

/// <summary>
/// Redaction of the raw failing command line. This is the one path by which a credential can reach the
/// database — an <c>AUTH PLAIN &lt;base64&gt;</c> whose payload is malformed fails in the parser like any
/// other command and carries the whole line — so the tests here are the guard on that, not a formatting
/// check.
/// </summary>
[TestClass]
public class RejectionBufferTests
{
    private static string? Redact(string line) => RejectionBuffer.Redact(Encoding.ASCII.GetBytes(line));

    [TestMethod]
    [TestCategory("Unit")]
    public void Redact_KeepsAnOrdinaryCommandVerbatim()
    {
        Assert.AreEqual("MAIL FROM:<broken@@example.com>", Redact("MAIL FROM:<broken@@example.com>"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Redact_StripsThePayloadOfAnInlineAuthPlain()
    {
        // dGVzdAB0ZXN0AHNlY3JldA== decodes to "test\0test\0secret".
        var redacted = Redact("AUTH PLAIN dGVzdAB0ZXN0AHNlY3JldA==");

        Assert.AreEqual("AUTH PLAIN [redacted]", redacted);
        StringAssert.Contains(redacted!, "PLAIN", "the mechanism is diagnostic and is kept");
        Assert.IsFalse(redacted!.Contains("dGVzdA"), "the credential must never reach storage");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Redact_IsCaseInsensitiveAboutTheVerb()
    {
        Assert.AreEqual("AUTH LOGIN [redacted]", Redact("auth login dXNlcg=="));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Redact_DropsAnUnrecognisedAuthTokenRatherThanEchoingIt()
    {
        // "AUTH <base64>" with no mechanism: the second token could BE the credential, so it is not echoed.
        Assert.AreEqual("AUTH [redacted]", Redact("AUTH dGVzdAB0ZXN0AHNlY3JldA=="));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Redact_TreatsALeadingSpaceAsPartOfTheAuthVerb()
    {
        // A malformed line may not start flush at the verb; the prefix check must not be evaded by it.
        Assert.AreEqual("AUTH PLAIN [redacted]", Redact("   AUTH PLAIN dGVzdAB0ZXN0AHNlY3JldA=="));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Redact_EscapesNonPrintableBytesInsteadOfStoringThem()
    {
        var redacted = RejectionBuffer.Redact([0x4D, 0x41, 0x49, 0x4C, 0x00, 0xFF, 0x0D, 0x0A]);

        Assert.AreEqual("MAIL\\x00\\xFF\\r\\n", redacted);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Redact_CapsALongLine()
    {
        var redacted = Redact("MAIL FROM:<" + new string('a', 2000) + ">");

        Assert.IsTrue(redacted!.Length <= 600, $"must fit the column, was {redacted.Length}");
        StringAssert.EndsWith(redacted, "[truncated]");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Redact_ReturnsNullWhenThereIsNothingToStore()
    {
        Assert.IsNull(RejectionBuffer.Redact(null));
        Assert.IsNull(RejectionBuffer.Redact([]));
    }
}

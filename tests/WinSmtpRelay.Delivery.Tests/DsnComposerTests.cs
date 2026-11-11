using System.Text;
using MimeKit;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Delivery.Dsn;

namespace WinSmtpRelay.Delivery.Tests;

/// <summary>T1074: the DSN is a well-formed RFC 3464 report that carries the original headers, not the content.</summary>
[TestClass]
public class DsnComposerTests
{
    private static readonly byte[] Original = Encoding.ASCII.GetBytes(
        "From: <alice@tenant.test>\r\nTo: <bob@a.test>\r\nSubject: Invoice 42\r\nMessage-ID: <inv42@tenant.test>\r\n\r\nSECRET BODY\r\n");

    private static MimeMessage Compose(params DsnRecipient[] recipients) => DsnComposer.Compose(new DsnInput(
        "relay.example.com", "alice@tenant.test", "<inv42@tenant.test>",
        new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero), Original, recipients,
        new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero)));

    [TestMethod]
    [TestCategory("Unit")]
    public void Report_HasTheRfc3464Structure()
    {
        var dsn = Compose(new DsnRecipient("bob@a.test", "550", "5.1.1 <bob@a.test>: user unknown", "mx.a.test:25"));

        Assert.AreEqual("MAILER-DAEMON@tenant.test", dsn.From.Mailboxes.Single().Address, "From is in the sender's domain, so DKIM signs it as the tenant");
        Assert.AreEqual("alice@tenant.test", dsn.To.Mailboxes.Single().Address);
        Assert.AreEqual("auto-replied", dsn.Headers["Auto-Submitted"]);
        Assert.IsInstanceOfType<MultipartReport>(dsn.Body);

        var report = (MultipartReport)dsn.Body!;
        Assert.AreEqual("delivery-status", report.ReportType);
        Assert.IsInstanceOfType<TextPart>(report[0], "the first part is human-readable");
        var status = (MessageDeliveryStatus)report[1];
        Assert.AreEqual("dns; relay.example.com", status.StatusGroups[0]["Reporting-MTA"]);
        var recipient = status.StatusGroups[1];
        Assert.AreEqual("rfc822; bob@a.test", recipient["Final-Recipient"]);
        Assert.AreEqual("failed", recipient["Action"]);
        Assert.AreEqual("5.1.1", recipient["Status"]);
        Assert.AreEqual("dns; mx.a.test", recipient["Remote-MTA"]);
        StringAssert.StartsWith(recipient["Diagnostic-Code"]!, "smtp; 550 5.1.1");

        var headers = (TextRfc822Headers)report[2];
        Assert.AreEqual("Invoice 42", headers.Message!.Subject);

        using var stream = new MemoryStream();
        dsn.WriteTo(stream);
        Assert.IsFalse(Encoding.UTF8.GetString(stream.ToArray()).Contains("SECRET BODY"), "the content never travels back");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void StatusOf_UsesTheEnhancedCode_OrTheReplyClass()
    {
        Assert.AreEqual("5.2.2", DsnComposer.StatusOf(new DsnRecipient("a@b", "552", "5.2.2 mailbox full", null)));
        Assert.AreEqual("5.0.0", DsnComposer.StatusOf(new DsnRecipient("a@b", "550", "no such user", null)));
        Assert.AreEqual("4.0.0", DsnComposer.StatusOf(new DsnRecipient("a@b", "421", "timeout", null)));
        Assert.AreEqual("5.7.1", DsnComposer.StatusOf(new DsnRecipient("a@b", "550", null, null, Suppressed: true)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsDeliveryReport_RecognisesADsn()
    {
        Assert.IsTrue(DsnComposer.IsDeliveryReport(DsnComposer.ComposeBytes(new DsnInput(
            "relay.example.com", "alice@tenant.test", "<x>", DateTimeOffset.UtcNow, Original,
            [new DsnRecipient("bob@a.test", "550", "x", null)], DateTimeOffset.UtcNow))));
        Assert.IsFalse(DsnComposer.IsDeliveryReport(Original));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void HeaderBlock_StopsAtTheBlankLine()
    {
        Assert.AreEqual("From: <alice@tenant.test>\r\nTo: <bob@a.test>\r\nSubject: Invoice 42\r\nMessage-ID: <inv42@tenant.test>\r\n",
            Encoding.ASCII.GetString(DsnComposer.HeaderBlock(Original)));
    }
}

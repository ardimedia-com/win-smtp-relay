using MailKit.Net.Smtp;
using MailKit;
using WinSmtpRelay.Delivery;

namespace WinSmtpRelay.Delivery.Tests;

[TestClass]
public class MxFailureMessageTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void ProtocolRejection_NamesRejectingHostAndQuotesServer()
    {
        // A reached-and-rejected MX must read differently from an unreachable one — conflating the two
        // sends the Journal reader down the wrong debugging path (DNS instead of SMTP protocol).
        var ex = new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode,
            SmtpStatusCode.MailboxUnavailable, "Is neither a FQDN nor a IP literal");
        var msg = SmtpDeliveryService.BuildMxFailureMessage("adon.li", ex, "mx01.adon.li", 30);

        Assert.AreEqual("mx01.adon.li rejected the delivery: Is neither a FQDN nor a IP literal", msg);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Timeout_ReportsUnreachableWithTimeout()
    {
        var msg = SmtpDeliveryService.BuildMxFailureMessage("example.com", new OperationCanceledException(), null, 30);
        Assert.AreEqual("No MX host for domain example.com could be reached: the connection timed out after 30s", msg);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NoMxRecords_ReportsUnreachable()
    {
        var msg = SmtpDeliveryService.BuildMxFailureMessage("example.com", null, null, 30);
        Assert.AreEqual("No MX host for domain example.com could be reached (no usable MX records)", msg);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ConnectionFailure_ReportsUnreachableWithReason()
    {
        var msg = SmtpDeliveryService.BuildMxFailureMessage("example.com", new IOException("Connection refused"), null, 30);
        Assert.AreEqual("No MX host for domain example.com could be reached: Connection refused", msg);
    }
}

using MailKit.Security;
using WinSmtpRelay.Delivery;

namespace WinSmtpRelay.Delivery.Tests;

/// <summary>
/// Verifies how a Send Connector's TLS flags map to a MailKit transport option — in particular that
/// "Require TLS" is enforced (mandatory STARTTLS) rather than silently ignored.
/// </summary>
[TestClass]
public class TlsOptionTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void RequireTls_IsMandatoryStartTls()
        => Assert.AreEqual(SecureSocketOptions.StartTls,
            SmtpDeliveryService.ResolveTlsOption(opportunisticTls: false, requireTls: true));

    [TestMethod]
    [TestCategory("Unit")]
    public void RequireTls_TakesPrecedenceOverOpportunistic()
        => Assert.AreEqual(SecureSocketOptions.StartTls,
            SmtpDeliveryService.ResolveTlsOption(opportunisticTls: true, requireTls: true));

    [TestMethod]
    [TestCategory("Unit")]
    public void Opportunistic_IsStartTlsWhenAvailable()
        => Assert.AreEqual(SecureSocketOptions.StartTlsWhenAvailable,
            SmtpDeliveryService.ResolveTlsOption(opportunisticTls: true, requireTls: false));

    [TestMethod]
    [TestCategory("Unit")]
    public void NeitherFlag_IsNone()
        => Assert.AreEqual(SecureSocketOptions.None,
            SmtpDeliveryService.ResolveTlsOption(opportunisticTls: false, requireTls: false));
}

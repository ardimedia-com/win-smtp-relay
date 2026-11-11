using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Tests;

/// <summary>T1075: only a rejection that says the address does not exist suppresses.</summary>
[TestClass]
public class SuppressionPolicyTests
{
    private static DeliveryResult Rejected(string code, string text, bool viaSmartHost = false, bool rcpt = true) => new()
    {
        Recipient = "x@example.com",
        StatusCode = code,
        StatusMessage = text,
        RecipientRejected = rcpt,
        ViaSmartHost = viaSmartHost,
    };

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("550", "5.1.1 <x@example.com>: Recipient address rejected: User unknown in virtual mailbox table")]
    [DataRow("550", "5.1.1 The email account that you tried to reach does not exist.")]
    [DataRow("550", "5.1.10 RESOLVER.ADR.RecipientNotFound; Recipient not found by SMTP address lookup")]
    [DataRow("550", "5.2.1 The email account that you tried to reach is disabled.")]
    [DataRow("553", "5.1.3 Invalid recipient address syntax")]
    [DataRow("550", "No such user here")]
    [DataRow("551", "User not local")]
    [DataRow("550", "mailbox unavailable")]
    public void AddressDoesNotExist_Suppresses(string code, string text) =>
        Assert.IsTrue(SuppressionPolicy.ShouldSuppress(Rejected(code, text)), $"{code} {text}");

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("550", "5.7.1 Service unavailable; client host [203.0.113.5] blocked using Spamhaus")]
    [DataRow("554", "5.7.1 Relay access denied")]
    [DataRow("550", "5.7.606 Access denied, banned sending IP")]
    [DataRow("550", "Message rejected due to local policy")]
    [DataRow("550", "Rejected: your IP is listed on a DNSBL / RBL")]
    [DataRow("552", "5.2.2 Mailbox full")]
    [DataRow("550", "5.1.1 user unknown — but also: SPF check failed")]
    [DataRow("550", "5.1.8 Bad sender's system address")]
    [DataRow("554", "5.4.1 Recipient address rejected: Access denied")]
    [DataRow("421", "4.7.0 Try again later")]
    [DataRow("450", "4.2.1 Mailbox busy")]
    [DataRow("554", "Transaction failed")]
    public void PolicyOrOtherRejections_DoNotSuppress(string code, string text) =>
        Assert.IsFalse(SuppressionPolicy.ShouldSuppress(Rejected(code, text)), $"{code} {text}");

    [TestMethod]
    [TestCategory("Unit")]
    public void SmartHostVerdict_NeverSuppresses() =>
        Assert.IsFalse(SuppressionPolicy.ShouldSuppress(Rejected("550", "5.1.1 User unknown", viaSmartHost: true)));

    [TestMethod]
    [TestCategory("Unit")]
    public void TransactionWideFailure_NeverSuppresses() =>
        Assert.IsFalse(SuppressionPolicy.ShouldSuppress(Rejected("550", "5.1.1 User unknown", rcpt: false)));

    [TestMethod]
    [TestCategory("Unit")]
    public void ExtractEnhancedCode_FindsTheFirstCode()
    {
        Assert.AreEqual("5.1.1", SuppressionPolicy.ExtractEnhancedCode("550 5.1.1 <a@b> unknown"));
        Assert.AreEqual("4.4.1", SuppressionPolicy.ExtractEnhancedCode("4.4.1 connection refused"));
        Assert.IsNull(SuppressionPolicy.ExtractEnhancedCode("550 mailbox unavailable"));
        Assert.IsNull(SuppressionPolicy.ExtractEnhancedCode("version 10.0.1.5 ok"));
    }
}

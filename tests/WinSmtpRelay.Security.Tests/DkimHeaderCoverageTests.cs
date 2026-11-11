using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Security.Tests;

/// <summary>
/// M8: the signature covers every header a recipient reads or a client threads by, and oversigns From,
/// To and Subject so a replay cannot add a second instance under our valid signature.
/// </summary>
[TestClass]
public class DkimHeaderCoverageTests
{
    private sealed class FixedKeyLocator(AsymmetricKeyParameter key) : DkimPublicKeyLocatorBase
    {
        public override AsymmetricKeyParameter LocatePublicKey(string methods, string domain, string selector, CancellationToken cancellationToken = default) => key;

        public override Task<AsymmetricKeyParameter> LocatePublicKeyAsync(string methods, string domain, string selector, CancellationToken cancellationToken = default) =>
            Task.FromResult(key);
    }

    private static MimeMessage NewMessage()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("R", "r@other.com"));
        message.Cc.Add(new MailboxAddress("C", "c@other.com"));
        message.ReplyTo.Add(new MailboxAddress("Reply", "reply@example.com"));
        message.Subject = "Invoice 42";
        message.Date = DateTimeOffset.UtcNow;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        message.MimeVersion = new Version(1, 0);
        message.Body = new TextPart("plain") { Text = "Please pay." };
        return message;
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildSignedHeaderList_CoversPresentHeaders_AndOversignsFromToSubject()
    {
        var list = DkimSigningService.BuildSignedHeaderList(NewMessage());

        Assert.AreEqual(2, list.Count(h => h == "From"), "From is listed once more than it occurs");
        Assert.AreEqual(2, list.Count(h => h == "To"));
        Assert.AreEqual(2, list.Count(h => h == "Subject"));
        CollectionAssert.Contains(list, "Cc");
        CollectionAssert.Contains(list, "Reply-To");
        CollectionAssert.Contains(list, "Message-ID");
        CollectionAssert.Contains(list, "MIME-Version");
        CollectionAssert.Contains(list, "Content-Type");
        CollectionAssert.DoesNotContain(list, "In-Reply-To", "absent headers other than the oversigned ones are not listed");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SignedMessage_Verifies_AndAPrependedSecondFromBreaksIt()
    {
        using var rsa = RSA.Create(2048);
        var keyPath = Path.Combine(Path.GetTempPath(), $"dkim_cov_{Guid.NewGuid()}.pem");
        File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
        try
        {
            var service = new DkimSigningService(Options.Create(new DkimOptions
            {
                Enabled = true,
                Domains = [new DkimDomainConfig { Domain = "example.com", Selector = "s1", PrivateKeyPath = keyPath }]
            }), NullLogger<DkimSigningService>.Instance);

            var message = NewMessage();
            service.Sign(message, TenantDefaults.DefaultTenantId, null);

            var signature = message.Headers[message.Headers.IndexOf(HeaderId.DkimSignature)];
            StringAssert.Contains(signature.Value.Replace(" ", "", StringComparison.Ordinal), "from:from");

            var publicKey = ((AsymmetricCipherKeyPair)new PemReader(new StringReader(rsa.ExportRSAPrivateKeyPem())).ReadObject()).Public;
            var verifier = new DkimVerifier(new FixedKeyLocator(publicKey));

            Assert.IsTrue(await verifier.VerifyAsync(message, signature), "the untouched message verifies");

            message.Headers.Insert(0, new Header(HeaderId.From, "Bank <ceo@attacker.example>"));
            Assert.IsFalse(await verifier.VerifyAsync(message, signature), "a second From breaks the oversigned signature");
        }
        finally
        {
            File.Delete(keyPath);
        }
    }
}

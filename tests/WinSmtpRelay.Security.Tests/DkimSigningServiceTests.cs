using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Security.Tests;

[TestClass]
public class DkimSigningServiceTests
{
    private static string CreateTestRsaKey()
    {
        // Generate a 2048-bit RSA key in PEM format for testing
        var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var path = Path.Combine(Path.GetTempPath(), $"dkim_test_{Guid.NewGuid()}.pem");
        File.WriteAllText(path, pem);
        return path;
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sign_WhenDisabled_DoesNothing()
    {
        var options = Options.Create(new DkimOptions { Enabled = false });
        var service = new DkimSigningService(options, NullLogger<DkimSigningService>.Instance);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Test", "test@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = "Test";
        message.Body = new TextPart("plain") { Text = "Hello" };

        service.Sign(message, TenantDefaults.DefaultTenantId, null);

        // No DKIM-Signature header should be added
        Assert.IsNull(message.Headers[HeaderId.DkimSignature]);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sign_WhenEnabledWithMatchingDomain_AddsDkimSignature()
    {
        var keyPath = CreateTestRsaKey();
        try
        {
            var options = Options.Create(new DkimOptions
            {
                Enabled = true,
                Domains =
                [
                    new DkimDomainConfig
                    {
                        Domain = "example.com",
                        Selector = "test",
                        PrivateKeyPath = keyPath
                    }
                ]
            });
            var service = new DkimSigningService(options, NullLogger<DkimSigningService>.Instance);

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Test", "test@example.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@other.com"));
            message.Subject = "Test Subject";
            message.Date = DateTimeOffset.UtcNow;
            message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
            message.Body = new TextPart("plain") { Text = "Hello World" };

            service.Sign(message, TenantDefaults.DefaultTenantId, null);

            var dkimHeader = message.Headers[HeaderId.DkimSignature];
            Assert.IsNotNull(dkimHeader, "DKIM-Signature header should be present");
            Assert.IsTrue(dkimHeader.Contains("d=example.com"), "DKIM signature should contain the domain");
            Assert.IsTrue(dkimHeader.Contains("s=test"), "DKIM signature should contain the selector");
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sign_WhenDomainNotConfigured_DoesNothing()
    {
        var keyPath = CreateTestRsaKey();
        try
        {
            var options = Options.Create(new DkimOptions
            {
                Enabled = true,
                Domains =
                [
                    new DkimDomainConfig
                    {
                        Domain = "example.com",
                        Selector = "test",
                        PrivateKeyPath = keyPath
                    }
                ]
            });
            var service = new DkimSigningService(options, NullLogger<DkimSigningService>.Instance);

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Test", "test@otherdomain.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
            message.Subject = "Test";
            message.Body = new TextPart("plain") { Text = "Hello" };

            service.Sign(message, TenantDefaults.DefaultTenantId, null);

            Assert.IsNull(message.Headers[HeaderId.DkimSignature]);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sign_WithTenantDkimKey_SignsUsingThatKey()
    {
        var keyPath = CreateTestRsaKey();
        try
        {
            // Config DKIM disabled — signing comes purely from the tenant's DB key.
            var service = new DkimSigningService(Options.Create(new DkimOptions { Enabled = false }), NullLogger<DkimSigningService>.Instance);
            var dkim = new DkimDomain { Domain = "tenant5.example", Selector = "t5", PrivateKeyPath = keyPath, TenantId = 5 };

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("T5", "user@tenant5.example"));
            message.To.Add(new MailboxAddress("R", "r@other.com"));
            message.Subject = "Hi";
            message.Date = DateTimeOffset.UtcNow;
            message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
            message.Body = new TextPart("plain") { Text = "Body" };

            service.Sign(message, 5, dkim);

            var dkimHeader = message.Headers[HeaderId.DkimSignature];
            Assert.IsNotNull(dkimHeader);
            Assert.IsTrue(dkimHeader.Contains("d=tenant5.example"));
            Assert.IsTrue(dkimHeader.Contains("s=t5"));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sign_NonDefaultTenantWithoutKey_DoesNotUseDefaultTenantConfigSigner()
    {
        var keyPath = CreateTestRsaKey();
        try
        {
            // example.com is configured for the DEFAULT tenant via config.
            var service = new DkimSigningService(Options.Create(new DkimOptions
            {
                Enabled = true,
                Domains = [new DkimDomainConfig { Domain = "example.com", Selector = "test", PrivateKeyPath = keyPath }]
            }), NullLogger<DkimSigningService>.Instance);

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Intruder", "user@example.com"));
            message.To.Add(new MailboxAddress("R", "r@other.com"));
            message.Subject = "Hi";
            message.Body = new TextPart("plain") { Text = "Body" };

            // Tenant 5 (not the default tenant) with no DKIM key of its own MUST NOT sign with the
            // default tenant's config key — no cross-tenant signing.
            service.Sign(message, tenantId: 5, tenantDkim: null);

            Assert.IsNull(message.Headers[HeaderId.DkimSignature]);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsConfiguredForDomain_ReturnsTrueForConfiguredDomain()
    {
        var keyPath = CreateTestRsaKey();
        try
        {
            var options = Options.Create(new DkimOptions
            {
                Enabled = true,
                Domains =
                [
                    new DkimDomainConfig
                    {
                        Domain = "example.com",
                        Selector = "default",
                        PrivateKeyPath = keyPath
                    }
                ]
            });
            var service = new DkimSigningService(options, NullLogger<DkimSigningService>.Instance);

            Assert.IsTrue(service.IsConfiguredForDomain("example.com"));
            Assert.IsFalse(service.IsConfiguredForDomain("other.com"));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Constructor_WhenKeyFileNotFound_LogsErrorAndContinues()
    {
        var options = Options.Create(new DkimOptions
        {
            Enabled = true,
            Domains =
            [
                new DkimDomainConfig
                {
                    Domain = "example.com",
                    Selector = "test",
                    PrivateKeyPath = @"C:\nonexistent\key.pem"
                }
            ]
        });

        // Should not throw
        var service = new DkimSigningService(options, NullLogger<DkimSigningService>.Instance);
        Assert.IsFalse(service.IsConfiguredForDomain("example.com"));
    }
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Security.Tests;

[TestClass]
public class CertificateLoaderTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void LoadCertificate_NothingConfigured_ReturnsNull()
    {
        var options = Options.Create(new TlsOptions());
        var loader = new CertificateLoader(options, NullLogger<CertificateLoader>.Instance);

        var cert = loader.LoadCertificate();
        Assert.IsNull(cert);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LoadCertificate_FileNotFound_ReturnsNull()
    {
        var options = Options.Create(new TlsOptions
        {
            CertificatePath = @"C:\nonexistent\cert.pfx"
        });
        var loader = new CertificateLoader(options, NullLogger<CertificateLoader>.Instance);

        var cert = loader.LoadCertificate();
        Assert.IsNull(cert);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LoadCertificate_ValidPfx_ReturnsCertificate()
    {
        // Create a self-signed cert as PFX
        var pfxPath = Path.Combine(Path.GetTempPath(), $"test_cert_{Guid.NewGuid()}.pfx");
        const string password = "testpass";

        try
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var selfSigned = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
            File.WriteAllBytes(pfxPath, selfSigned.Export(X509ContentType.Pfx, password));

            var options = Options.Create(new TlsOptions
            {
                CertificatePath = pfxPath,
                CertificatePassword = password
            });
            var loader = new CertificateLoader(options, NullLogger<CertificateLoader>.Instance);

            var cert = loader.LoadCertificate();
            Assert.IsNotNull(cert);
            Assert.AreEqual("CN=Test", cert.Subject);
        }
        finally
        {
            File.Delete(pfxPath);
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void WritePfx(string path, string subject, string password)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSigned = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(path, selfSigned.Export(X509ContentType.Pfx, password));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LoadCertificate_IsCached_ThenReloadedWhenTheFileChanges()
    {
        var pfxPath = Path.Combine(Path.GetTempPath(), $"test_cert_{Guid.NewGuid()}.pfx");
        const string password = "testpass";
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));

        try
        {
            WritePfx(pfxPath, "CN=First", password);
            using var loader = new CertificateLoader(
                Options.Create(new TlsOptions { CertificatePath = pfxPath, CertificatePassword = password }),
                NullLogger<CertificateLoader>.Instance, clock);

            var first = loader.LoadCertificate();
            Assert.AreSame(first, loader.LoadCertificate(), "repeated calls (each TLS handshake, each health check) reuse one load");

            // A renewed certificate dropped over the old file is served after the next re-check.
            WritePfx(pfxPath, "CN=Renewed", password);
            File.SetLastWriteTimeUtc(pfxPath, DateTime.UtcNow.AddMinutes(5));
            Assert.AreSame(first, loader.LoadCertificate(), "not re-checked before the interval");

            clock.Now += CertificateLoader.RecheckInterval;
            var renewed = loader.LoadCertificate();
            Assert.IsNotNull(renewed);
            Assert.AreEqual("CN=Renewed", renewed.Subject);
        }
        finally
        {
            File.Delete(pfxPath);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LoadCertificate_FileDisappears_KeepsTheLastGoodCertificate()
    {
        var pfxPath = Path.Combine(Path.GetTempPath(), $"test_cert_{Guid.NewGuid()}.pfx");
        const string password = "testpass";
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));

        WritePfx(pfxPath, "CN=Kept", password);
        using var loader = new CertificateLoader(
            Options.Create(new TlsOptions { CertificatePath = pfxPath, CertificatePassword = password }),
            NullLogger<CertificateLoader>.Instance, clock);

        var first = loader.LoadCertificate();
        File.Delete(pfxPath);
        clock.Now += CertificateLoader.RecheckInterval;

        Assert.AreSame(first, loader.LoadCertificate());
    }
}

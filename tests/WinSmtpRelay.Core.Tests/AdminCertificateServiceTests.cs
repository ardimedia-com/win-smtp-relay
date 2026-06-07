using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class AdminCertificateServiceTests
{
    private RelayDbContext _db = null!;
    private AdminCertificateService _svc = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new RelayDbContext(options, new CurrentTenant());
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _svc = new AdminCertificateService(_db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Import_StoresMetadata_AndLoadsCertificateWithPrivateKey()
    {
        var (pfx, password) = CreatePfx("CN=admin.test.local");

        var info = await _svc.ImportAsync(pfx, password);
        Assert.IsTrue(info.HasImportedCertificate);
        StringAssert.Contains(info.Subject, "admin.test.local");
        Assert.IsNotNull(info.Thumbprint);

        _db.ChangeTracker.Clear();
        Assert.IsTrue((await _svc.GetAsync()).HasImportedCertificate);

        using var loaded = await _svc.LoadImportedAsync();
        Assert.IsNotNull(loaded);
        Assert.IsTrue(loaded!.HasPrivateKey, "the loaded certificate must retain its private key");
        Assert.AreEqual(info.Thumbprint, loaded.Thumbprint);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Clear_RevertsToNoImportedCertificate()
    {
        var (pfx, password) = CreatePfx("CN=admin.test.local");
        await _svc.ImportAsync(pfx, password);

        await _svc.ClearAsync();

        _db.ChangeTracker.Clear();
        Assert.IsFalse((await _svc.GetAsync()).HasImportedCertificate);
        Assert.IsNull(await _svc.LoadImportedAsync());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Import_InvalidBytes_ThrowsInvalidOperation()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _svc.ImportAsync([1, 2, 3, 4], null));
    }

    private static (byte[] pfx, string password) CreatePfx(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        const string password = "test-password";
        return (cert.Export(X509ContentType.Pfx, password), password);
    }
}

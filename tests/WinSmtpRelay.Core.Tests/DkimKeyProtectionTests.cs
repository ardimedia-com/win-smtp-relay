using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// Verifies the DKIM private-key protection: the EF value converter encrypts PrivateKeyPem at rest
/// (DPAPI on Windows) yet round-trips transparently, and the static helper handles legacy plaintext.
/// </summary>
[TestClass]
public class DkimKeyProtectionTests
{
    private RelayDbContext _db = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new RelayDbContext(options, new CurrentTenant());
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PrivateKeyPem_RoundTripsThroughDatabase()
    {
        const string pem = "-----BEGIN PRIVATE KEY-----\nMIIBVgIBADAN\n-----END PRIVATE KEY-----\n";

        _db.DkimDomains.Add(new DkimDomain { Domain = "example.com", Selector = "s1", PrivateKeyPem = pem });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var reloaded = await _db.DkimDomains.AsNoTracking().FirstAsync(d => d.Domain == "example.com");

        // The converter must surface the original plaintext PEM transparently.
        Assert.AreEqual(pem, reloaded.PrivateKeyPem);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PrivateKeyPem_IsNotStoredAsPlaintext_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI encryption only applies on Windows.");
            return;
        }

        const string pem = "-----BEGIN PRIVATE KEY-----\nSECRETKEYMATERIAL\n-----END PRIVATE KEY-----\n";

        _db.DkimDomains.Add(new DkimDomain { Domain = "secret.com", Selector = "s1", PrivateKeyPem = pem });
        await _db.SaveChangesAsync();

        // Read the raw stored column value, bypassing the converter, to assert it is encrypted.
        var conn = _db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PrivateKeyPem FROM DkimDomains WHERE Domain = 'secret.com'";
        var raw = (string?)await cmd.ExecuteScalarAsync();

        Assert.IsNotNull(raw);
        Assert.IsTrue(raw!.StartsWith("dpapi:", StringComparison.Ordinal), "Stored value must carry the DPAPI marker.");
        Assert.IsFalse(raw.Contains("SECRETKEYMATERIAL", StringComparison.Ordinal), "Plaintext key material must not be stored.");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Protect_NullOrEmpty_ReturnedUnchanged()
    {
        Assert.IsNull(DkimKeyProtector.Protect(null));
        Assert.AreEqual("", DkimKeyProtector.Protect(""));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Unprotect_LegacyPlaintext_ReturnedUnchanged()
    {
        // No marker -> treated as pre-existing plaintext and returned verbatim (lazy migration).
        const string legacy = "-----BEGIN PRIVATE KEY-----\nLEGACY\n-----END PRIVATE KEY-----\n";
        Assert.AreEqual(legacy, DkimKeyProtector.Unprotect(legacy));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ProtectThenUnprotect_RoundTrips_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI encryption only applies on Windows.");
            return;
        }

        const string pem = "-----BEGIN PRIVATE KEY-----\nROUNDTRIP\n-----END PRIVATE KEY-----\n";
        var protectedValue = DkimKeyProtector.Protect(pem);

        Assert.IsNotNull(protectedValue);
        Assert.IsTrue(protectedValue!.StartsWith("dpapi:", StringComparison.Ordinal));
        Assert.AreNotEqual(pem, protectedValue);
        Assert.AreEqual(pem, DkimKeyProtector.Unprotect(protectedValue));
    }
}

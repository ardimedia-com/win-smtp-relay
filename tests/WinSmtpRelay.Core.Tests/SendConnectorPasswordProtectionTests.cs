using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// Verifies that the upstream smart-host / submission password (e.g. a Brevo SMTP key) is encrypted at
/// rest by the EF value converter (DPAPI on Windows) yet round-trips transparently for callers — the
/// same protection applied to DKIM keys, via the shared <see cref="SecretProtector"/>.
/// </summary>
[TestClass]
public class SendConnectorPasswordProtectionTests
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
    public async Task EncryptedPassword_RoundTripsThroughDatabase()
    {
        const string secret = "xsmtpsib-brevo-smtp-key-EXAMPLE";

        _db.SendConnectors.Add(new SendConnector
        {
            Name = "Brevo",
            SmartHost = "smtp-relay.brevo.com",
            SmartHostPort = 587,
            Username = "user@example.com",
            EncryptedPassword = secret
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var reloaded = await _db.SendConnectors.AsNoTracking().FirstAsync(c => c.Name == "Brevo");

        // The converter must surface the original plaintext password transparently.
        Assert.AreEqual(secret, reloaded.EncryptedPassword);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EncryptedPassword_IsNotStoredAsPlaintext_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI encryption only applies on Windows.");
            return;
        }

        const string secret = "BREVOSMTPKEYSECRET";

        _db.SendConnectors.Add(new SendConnector
        {
            Name = "BrevoSecret",
            SmartHost = "smtp-relay.brevo.com",
            SmartHostPort = 587,
            Username = "user@example.com",
            EncryptedPassword = secret
        });
        await _db.SaveChangesAsync();

        // Read the raw stored column value, bypassing the converter, to assert it is encrypted.
        var conn = _db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EncryptedPassword FROM SendConnectors WHERE Name = 'BrevoSecret'";
        var raw = (string?)await cmd.ExecuteScalarAsync();

        Assert.IsNotNull(raw);
        Assert.IsTrue(raw!.StartsWith("dpapi:", StringComparison.Ordinal), "Stored value must carry the DPAPI marker.");
        Assert.IsFalse(raw.Contains(secret, StringComparison.Ordinal), "Plaintext password must not be stored.");
    }
}

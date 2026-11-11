using System.Text;
using WinSmtpRelay.DataLocation;
using WinSmtpRelay.Service.Update;
using WinSmtpRelay.Setup.CustomActions;

namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// Review area E: the one-time move of the data out of the program folder (T1076), the self-update package checks
/// (T1077, T1084), the installer input validation (H14) and the SYSTEM updater task definition.
/// </summary>
[TestClass]
public class LegacyDataMigratorTests
{
    private string root = null!;
    private string legacy = null!;
    private string data = null!;
    private readonly List<string> log = [];

    [TestInitialize]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "wsr-migration-" + Guid.NewGuid().ToString("N"));
        legacy = Path.Combine(root, "Program Files", "WinSmtpRelay");
        data = Path.Combine(root, "ProgramData", "Ardimedia", "WinSmtpRelay");
        Directory.CreateDirectory(legacy);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(root, true); } catch { /* best effort */ }
    }

    private LegacyDataMigrator Migrator() => new(log.Add);

    private void Legacy(string relative, string content)
    {
        var path = Path.Combine(legacy, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void SeedLegacyInstall()
    {
        Legacy("winsmtprelay.db", "db");
        Legacy("winsmtprelay.db-wal", "wal");
        Legacy("winsmtprelay.db-shm", "shm");
        Legacy("keys/key-1.xml", "k1");
        Legacy("admin-cert.pfx", "pfx");
        Legacy("appsettings.json", "{ \"a\": 1 }");
        Legacy("appsettings.Production.json", "{ \"AdminUi\": { \"Port\": 8125 } }");
        Legacy("WinSmtpRelay.Service.dll", "binary"); // never moved
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void MovesTheDataAndWritesTheMarker()
    {
        SeedLegacyInstall();

        var result = Migrator().Migrate(legacy, data);

        foreach (var file in new[] { "winsmtprelay.db", "winsmtprelay.db-wal", "winsmtprelay.db-shm", "admin-cert.pfx",
                     "appsettings.json", "appsettings.Production.json", Path.Combine("keys", "key-1.xml") })
        {
            Assert.IsTrue(File.Exists(Path.Combine(data, file)), file + " in the data folder");
            Assert.IsFalse(File.Exists(Path.Combine(legacy, file)), file + " gone from the program folder");
        }
        Assert.AreEqual("wal", File.ReadAllText(Path.Combine(data, "winsmtprelay.db-wal")));
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "WinSmtpRelay.Service.dll")), "binaries stay");
        Assert.IsFalse(Directory.Exists(Path.Combine(legacy, "keys")), "the empty legacy key folder is removed");
        Assert.IsTrue(LegacyDataMigrator.IsMigrated(data));
        Assert.AreEqual(7, result.Moved.Count);
        Assert.IsEmpty(result.Skipped);
        Assert.IsTrue(log.Any(l => l.Contains("moved")), "every step is logged");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RunsExactlyOnce()
    {
        SeedLegacyInstall();
        Migrator().Migrate(legacy, data);
        Migrator().Commit(data);

        Legacy("winsmtprelay.db", "a later file in the old place");
        var second = Migrator().Migrate(legacy, data);

        Assert.IsTrue(second.AlreadyMigrated);
        Assert.AreEqual("db", File.ReadAllText(Path.Combine(data, "winsmtprelay.db")));
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "winsmtprelay.db")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NeverOverwritesDataThatIsAlreadyInTheDataFolder()
    {
        SeedLegacyInstall();
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "winsmtprelay.db"), "newer");
        File.WriteAllText(Path.Combine(data, "appsettings.json"), "{ \"newer\": true }");

        var result = Migrator().Migrate(legacy, data);

        Assert.AreEqual("newer", File.ReadAllText(Path.Combine(data, "winsmtprelay.db")));
        Assert.AreEqual("{ \"newer\": true }", File.ReadAllText(Path.Combine(data, "appsettings.json")));
        // The database group stays together: its WAL must never end up next to a different database.
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "winsmtprelay.db")));
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "winsmtprelay.db-wal")));
        Assert.IsFalse(File.Exists(Path.Combine(data, "winsmtprelay.db-wal")));
        Assert.IsTrue(result.Skipped.Contains(Path.Combine(legacy, "winsmtprelay.db-wal")));
        // Unrelated files still move.
        Assert.IsTrue(File.Exists(Path.Combine(data, "admin-cert.pfx")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RollbackRestoresTheProgramFolder()
    {
        SeedLegacyInstall();
        Migrator().Migrate(legacy, data);

        Migrator().Rollback(data);

        Assert.AreEqual("db", File.ReadAllText(Path.Combine(legacy, "winsmtprelay.db")));
        Assert.AreEqual("wal", File.ReadAllText(Path.Combine(legacy, "winsmtprelay.db-wal")));
        Assert.AreEqual("k1", File.ReadAllText(Path.Combine(legacy, "keys", "key-1.xml")));
        Assert.IsFalse(File.Exists(Path.Combine(data, "winsmtprelay.db")));
        Assert.IsFalse(LegacyDataMigrator.IsMigrated(data), "the marker goes with the rollback");
        Assert.IsFalse(File.Exists(Path.Combine(data, LegacyDataMigrator.JournalFileName)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void FreshInstallWritesOnlyTheMarker()
    {
        Directory.Delete(legacy, true);

        var result = Migrator().Migrate(legacy, data);

        Assert.IsEmpty(result.Moved);
        Assert.IsTrue(LegacyDataMigrator.IsMigrated(data));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DevelopmentRunWithTheSameFolderDoesNothing()
    {
        SeedLegacyInstall();

        var result = Migrator().Migrate(legacy, legacy + Path.DirectorySeparatorChar);

        Assert.IsTrue(result.SameDirectory);
        Assert.IsFalse(LegacyDataMigrator.IsMigrated(legacy));
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "winsmtprelay.db")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void MovedSettingsCountAsModifiedForWindowsInstaller()
    {
        Legacy("appsettings.json", "{}");
        File.SetCreationTimeUtc(Path.Combine(legacy, "appsettings.json"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(legacy, "appsettings.json"), DateTime.UtcNow.AddMinutes(-5));

        Migrator().Migrate(legacy, data);

        var moved = Path.Combine(data, "appsettings.json");
        Assert.IsTrue(File.GetCreationTimeUtc(moved) < File.GetLastWriteTimeUtc(moved),
            "an unversioned file whose last write is not after its creation would be replaced by the MSI default");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void MovesDatabaseBackupsAndOrphanedSideFiles()
    {
        Legacy("winsmtprelay.db.bak-20260604", "backup");
        Legacy("old.db-wal", "orphan");

        Migrator().Migrate(legacy, data);

        Assert.IsTrue(File.Exists(Path.Combine(data, "winsmtprelay.db.bak-20260604")));
        Assert.IsTrue(File.Exists(Path.Combine(data, "old.db-wal")));
    }
}

[TestClass]
public class UpdatePackageIdentityTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void AcceptsOnlyWinSmtpRelaysUpgradeCodeAndName()
    {
        Assert.IsTrue(UpdatePackageIdentity.IsOurProduct("{2CBB38D8-5035-40DB-B30B-0E55F24EC496}", "WinSmtpRelay 1.0.0-beta1-build80", out _));
        Assert.IsTrue(UpdatePackageIdentity.IsOurProduct("{2cbb38d8-5035-40db-b30b-0e55f24ec496}", "WinSmtpRelay", out _));
        Assert.IsFalse(UpdatePackageIdentity.IsOurProduct("{00000000-5035-40DB-B30B-0E55F24EC496}", "WinSmtpRelay 1.0", out _));
        Assert.IsFalse(UpdatePackageIdentity.IsOurProduct("{2CBB38D8-5035-40DB-B30B-0E55F24EC496}", "WinSmtpRelayEvil 1.0", out _));
        Assert.IsFalse(UpdatePackageIdentity.IsOurProduct("{2CBB38D8-5035-40DB-B30B-0E55F24EC496}", "Other", out var reason));
        Assert.IsNotNull(reason);
        Assert.IsFalse(UpdatePackageIdentity.IsOurProduct(null, null, out _));
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("1.0.80", "1.0.0", true, DisplayName = "first build-numbered release over build79 (MSI 1.0.0)")]
    [DataRow("1.0.81", "1.0.80", true)]
    [DataRow("1.1.0", "1.0.99", true)]
    [DataRow("1.0.80", "1.0.80", false, DisplayName = "same build is not an update")]
    [DataRow("1.0.79", "1.0.80", false, DisplayName = "downgrade")]
    [DataRow("1.0.80.5", "1.0.80", false, DisplayName = "the fourth field does not count")]
    [DataRow("1.0.80", null, true, DisplayName = "nothing installed")]
    [DataRow("garbage", "1.0.0", false)]
    [DataRow("1.0.70000", "1.0.0", false, DisplayName = "build field above 65535")]
    [DataRow("1.0.-1", "1.0.0", false)]
    public void ComparesVersionsTheWayWindowsInstallerDoes(string candidate, string? installed, bool newer)
    {
        Assert.AreEqual(newer, UpdatePackageIdentity.IsNewer(candidate, installed, out _));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PinsTheReleaseCertificateThumbprint()
    {
        var pins = UpdatePackageIdentity.DefaultSignerThumbprints;
        Assert.IsTrue(UpdatePackageIdentity.IsPinnedThumbprint("11E81500C908EC01E783ACBBB2E00ABAAA603AC3", pins));
        Assert.IsTrue(UpdatePackageIdentity.IsPinnedThumbprint("11e81500c908ec01e783acbbb2e00abaaa603ac3", pins));
        Assert.IsTrue(UpdatePackageIdentity.IsPinnedThumbprint("11 E8 15 00 C9 08 EC 01 E7 83 AC BB B2 E0 0A BA AA 60 3A C3", pins));
        Assert.IsFalse(UpdatePackageIdentity.IsPinnedThumbprint("0000000000000000000000000000000000000000", pins));
        Assert.IsFalse(UpdatePackageIdentity.IsPinnedThumbprint(null, pins));
        Assert.IsFalse(UpdatePackageIdentity.IsPinnedThumbprint("11E81500", ["11E81500"]), "a truncated pin never matches");
        // Rollover: old and new certificate side by side.
        Assert.IsTrue(UpdatePackageIdentity.IsPinnedThumbprint("AB".PadRight(40, '0'), [.. pins, "AB".PadRight(40, '0')]));
    }
}

[TestClass]
public class InstallerInputTests
{
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("8025", true)]
    [DataRow("1", true)]
    [DataRow("65535", true)]
    [DataRow("0", false)]
    [DataRow("65536", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    [DataRow("+80", false)]
    [DataRow(" 80", false)]
    [DataRow("80a", false)]
    [DataRow("8025|DIR=C:\\Windows", false)]
    [DataRow("８０２５", false)] // full-width digits
    public void AdminPortMustBeAPlainNumberFrom1To65535(string? value, bool valid)
    {
        Assert.AreEqual(valid, InstallerInput.TryParsePort(value!, out _));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CustomActionDataCannotBeInjectedThroughAPropertyValue()
    {
        var ok = InstallerInput.ParseCustomActionData(@"DIR=C:\ProgramData\Ardimedia\WinSmtpRelay\|PORT=8025|NET=1", "DIR", "PORT", "NET");
        Assert.AreEqual("8025", ok["PORT"]);

        // A PORT value carrying "|DIR=..." would override the directory — rejected as a duplicate key.
        Assert.ThrowsExactly<FormatException>(() =>
            InstallerInput.ParseCustomActionData(@"DIR=C:\data\|PORT=8025|DIR=C:\Windows|NET=", "DIR", "PORT", "NET"));
        Assert.ThrowsExactly<FormatException>(() =>
            InstallerInput.ParseCustomActionData("DIR=x|PORT=1|EXTRA=y", "DIR", "PORT", "NET"));
        Assert.ThrowsExactly<FormatException>(() =>
            InstallerInput.ParseCustomActionData("DIR=x|garbage", "DIR"));
    }
}

[TestClass]
public class UpdateTaskDefinitionTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "winsmtprelay.slnx")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "repository root not found");
        return File.ReadAllText(Path.Combine([dir.FullName, .. parts]));
    }

    private static string Template => RepoFile("setup", "WinSmtpRelay.Setup", "updater", "update-task.xml");
    private static string Script => RepoFile("setup", "WinSmtpRelay.Setup", "updater", "Update-WinSmtpRelay.ps1");

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildsARegistrableTaskWithTheEncodedScript()
    {
        var xml = UpdateTaskDefinition.Build(Template, Script);

        Assert.StartsWith("<Task", xml);
        Assert.DoesNotContain(UpdateTaskDefinition.Placeholder, xml);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        XNamespaceAssert(doc);

        var encoded = UpdateTaskDefinition.EncodeCommand(Script);
        Assert.IsLessThanOrEqualTo(UpdateTaskDefinition.MaxEncodedCommandLength, encoded.Length);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Contains("Stop-Update 16", decoded, "the thumbprint pin is part of the executed script");
        Assert.DoesNotContain("\n#", decoded, "full-line comments are stripped");
    }

    private static void XNamespaceAssert(System.Xml.Linq.XDocument doc)
    {
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var args = doc.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Arguments")!.Value;
        Assert.Contains("-EncodedCommand ", args);
        Assert.AreEqual("S-1-5-18", doc.Root.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "UserId")!.Value);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TheSystemScriptPinsTheSameIdentityAsTheService()
    {
        var script = Script;
        Assert.Contains("$UpgradeCode = '" + UpdatePackageIdentity.UpgradeCode + "'", script);
        Assert.Contains("$ProductName = '" + UpdatePackageIdentity.ProductName + "'", script);
        Assert.Contains("$Organization = '" + UpdatePackageIdentity.SignerOrganization + "'", script);
        foreach (var thumbprint in UpdatePackageIdentity.DefaultSignerThumbprints)
            Assert.Contains("'" + thumbprint + "'", script);
        Assert.IsTrue(script.All(c => c < 128), "Windows PowerShell 5.1 script must be ASCII");
    }
}

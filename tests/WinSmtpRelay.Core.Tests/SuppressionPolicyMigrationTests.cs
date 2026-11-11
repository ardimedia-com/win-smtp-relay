using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// The migration <c>SuppressionPolicyAndQueueLease</c> runs at startup on live, populated databases. It must backfill
/// NextRetryUtc before making it non-nullable, lift only the policy/blocklist hard-bounce suppressions (and record
/// how many), move the old «keep for ever» suppression retention to 30 days, and turn DSNs on for existing tenants.
/// </summary>
[TestClass]
public class SuppressionPolicyMigrationTests
{
    private const string PreviousMigration = "20261019100407_ReportingScheduleLocalTime";

    private string _path = null!;

    [TestInitialize]
    public void Setup() => _path = Path.Combine(Path.GetTempPath(), $"wsr-migration-{Guid.NewGuid():N}.db");

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }

    // One provider for every context, as in the running service (Identity schema v3 adds the passkey table).
    private static readonly ServiceProvider AppServices = new ServiceCollection()
        .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .BuildServiceProvider();

    private static readonly Microsoft.Extensions.Caching.Memory.MemoryCache ModelCache = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

    private RelayDbContext NewContext()
    {
        var appServices = AppServices;
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite($"Data Source={_path}")
            .UseApplicationServiceProvider(appServices)
            // Own model cache: other tests in this process build the model without the Identity v3 tables, and
            // EF's shared model cache does not key on IdentityOptions.
            .UseMemoryCache(ModelCache)
            .Options;
        return new RelayDbContext(options, new CurrentTenant());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Migration_OnPopulatedDatabase_BackfillsAndLiftsPolicySuppressions()
    {
        await using (var db = NewContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "QueuedMessages" ("TenantId","MessageId","Sender","Recipients","RawMessage","SizeBytes","Status","RetryCount","CreatedUtc","NextRetryUtc")
                VALUES (1,'<a@t>','s@t','r@t',X'00',1,0,0,'2026-11-01T10:00:00.0000000Z',NULL),
                       (1,'<b@t>','s@t','r@t',X'00',1,0,1,'2026-11-01T10:00:00.0000000Z','2026-11-02T10:00:00.0000000Z');
                INSERT INTO "SuppressionEntries" ("TenantId","Address","Reason","Detail","CreatedUtc") VALUES
                    (1,'unknown@x.test',0,'550 5.1.1 user unknown','2026-11-01T10:00:00.0000000Z'),
                    (1,'blocked@x.test',0,'550 5.7.1 client host blocked using Spamhaus','2026-11-01T10:00:00.0000000Z'),
                    (1,'policy@x.test',0,'550 Message rejected due to local policy','2026-11-01T10:00:00.0000000Z'),
                    (1,'exo@x.test',0,'550 5.4.1 Recipient address rejected: Access denied. For more information see https://aka.ms/EXOSmtpErrors','2026-11-01T10:00:00.0000000Z'),
                    (1,'relaydenied@x.test',0,'554 5.4.1 Relay access denied','2026-11-01T10:00:00.0000000Z'),
                    (1,'manual@x.test',2,'550 5.7.1 blocked','2026-11-01T10:00:00.0000000Z');
                """);
        }

        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();

            var messages = await db.QueuedMessages.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
            Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), messages[0].NextRetryUtc, "a missing retry time is the creation time");
            Assert.AreEqual(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), messages[1].NextRetryUtc);
            Assert.IsTrue(messages.All(m => m.Origin == MessageOrigin.Submitted));

            CollectionAssert.AreEquivalent(new[] { "unknown@x.test", "exo@x.test", "manual@x.test" },
                await db.SuppressionEntries.Select(e => e.Address).ToListAsync(),
                "policy/blocklist hard bounces lifted; Exchange Online's unknown-recipient 5.4.1 and manual entries kept");
            var audit = await db.AdminAuditEvents.AsNoTracking().SingleAsync(e => e.Action == "suppression.removed");
            StringAssert.Contains(audit.Detail, "Lifted 3 ");

            Assert.AreEqual(30, (await db.DataRetentionSettings.AsNoTracking().SingleAsync()).SuppressionDays);
            Assert.IsTrue((await db.Tenants.AsNoTracking().SingleAsync()).DsnEnabled);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Migration_WithNothingToLift_WritesNoAuditEvent()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        Assert.IsFalse(await db.AdminAuditEvents.AnyAsync(e => e.Action == "suppression.removed"));
    }
}

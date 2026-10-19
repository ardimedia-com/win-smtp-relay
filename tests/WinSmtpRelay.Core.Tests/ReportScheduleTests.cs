using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

/// <summary>Report / self-check schedule from Settings → Reporting, and the upgrade of the old fixed-UTC setting.</summary>
[TestClass]
public class ReportScheduleTests
{
    private static TimeZoneInfo Zurich => TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [TestMethod]
    [TestCategory("Unit")]
    public void From_SelfCheckDefaultsToThirtyMinutesBeforeReport()
    {
        var schedule = ReportSchedule.From(new ReportingSettings { DailyTimeLocal = "06:00", TimeZoneId = "Europe/Zurich" });
        Assert.AreEqual(new TimeOnly(6, 0), schedule.ReportTime);
        Assert.AreEqual(new TimeOnly(5, 30), schedule.SelfCheckTime);
        Assert.IsTrue(schedule.SelfCheckFollowsReport);
        Assert.AreEqual("06:00 (Europe/Zurich)", schedule.Label(schedule.ReportTime));

        // Wraps past midnight.
        Assert.AreEqual(new TimeOnly(23, 45), ReportSchedule.DefaultSelfCheckFor(new TimeOnly(0, 15)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void From_ExplicitSelfCheckTime_IsKept()
    {
        var schedule = ReportSchedule.From(new ReportingSettings { DailyTimeLocal = "07:00", SelfCheckTimeLocal = "05:30", TimeZoneId = "Europe/Zurich" });
        Assert.AreEqual(new TimeOnly(5, 30), schedule.SelfCheckTime);
        Assert.IsFalse(schedule.SelfCheckFollowsReport);
        // 05:30 Zurich in winter = 04:30 UTC.
        Assert.AreEqual(Utc(2026, 11, 2, 4, 30), schedule.NextSelfCheckUtc(Utc(2026, 11, 1, 12, 0)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void UpgradeLegacy_KeepsTheInstant_ConvertedToLocalTime()
    {
        // Old setting "04:00" (UTC) read on a summer day: 04:00 UTC = 06:00 CEST → stored as 06:00 local,
        // which stays 06:00 local (05:00 UTC) after the change to winter time.
        var settings = new ReportingSettings { DailyTimeLocal = null, DailyTimeUtc = "04:00", LastDigestSentDate = new DateOnly(2026, 10, 19) };
        Assert.IsTrue(ReportSchedule.UpgradeLegacy(settings, Zurich, Utc(2026, 10, 19, 9, 0)));
        Assert.AreEqual("06:00", settings.DailyTimeLocal);
        Assert.IsNull(settings.SelfCheckTimeLocal, "the self-check follows the digest (30 min earlier)");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 19, 4, 0, 0, TimeSpan.Zero), settings.LastDigestSentUtc);

        settings.TimeZoneId = "Europe/Zurich";
        Assert.AreEqual(Utc(2026, 10, 26, 5, 0), ReportSchedule.From(settings).NextReportUtc(Utc(2026, 10, 25, 12, 0)));

        // Idempotent.
        Assert.IsFalse(ReportSchedule.UpgradeLegacy(settings, Zurich, Utc(2026, 12, 1, 9, 0)));
        Assert.AreEqual("06:00", settings.DailyTimeLocal);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void UpgradeLegacy_OldDefault0600Utc_Becomes0800Summer()
    {
        var settings = new ReportingSettings { DailyTimeLocal = null, DailyTimeUtc = "06:00" };
        ReportSchedule.UpgradeLegacy(settings, Zurich, Utc(2026, 10, 19, 9, 0));
        Assert.AreEqual("08:00", settings.DailyTimeLocal);

        var inWinter = new ReportingSettings { DailyTimeLocal = null, DailyTimeUtc = "06:00" };
        ReportSchedule.UpgradeLegacy(inWinter, Zurich, Utc(2026, 12, 1, 9, 0));
        Assert.AreEqual("07:00", inWinter.DailyTimeLocal);

        var serverInUtc = new ReportingSettings { DailyTimeLocal = null, DailyTimeUtc = "06:00" };
        ReportSchedule.UpgradeLegacy(serverInUtc, TimeZoneInfo.Utc, Utc(2026, 10, 19, 9, 0));
        Assert.AreEqual("06:00", serverInUtc.DailyTimeLocal);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsDigestDue_OncePerLocalDay()
    {
        var settings = new ReportingSettings { DailyTimeLocal = "06:00", TimeZoneId = "Europe/Zurich" };
        var schedule = ReportSchedule.From(settings);

        // Never sent: due once 06:00 local has passed today, not before.
        Assert.IsFalse(schedule.IsDigestDue(settings, Utc(2026, 10, 26, 4, 59)));
        Assert.IsTrue(schedule.IsDigestDue(settings, Utc(2026, 10, 26, 5, 0)));

        // Sent at 06:02 local → not again today, even after moving the time later.
        settings.LastDigestSentUtc = new DateTimeOffset(Utc(2026, 10, 26, 5, 2));
        settings.LastDigestSentDate = new DateOnly(2026, 10, 26);
        Assert.IsFalse(schedule.IsDigestDue(settings, Utc(2026, 10, 26, 12, 0)));
        settings.DailyTimeLocal = "14:00";
        Assert.IsFalse(ReportSchedule.From(settings).IsDigestDue(settings, Utc(2026, 10, 26, 13, 30)));

        // Next day at the time.
        settings.DailyTimeLocal = "06:00";
        Assert.IsTrue(schedule.IsDigestDue(settings, Utc(2026, 10, 27, 5, 1)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsDigestDue_AfterUpgrade_DoesNotResendTodaysDigest()
    {
        // Build78 sent today's digest at 04:00 UTC; the upgrade must not send it again.
        var settings = new ReportingSettings { DailyTimeLocal = null, DailyTimeUtc = "04:00", LastDigestSentDate = new DateOnly(2026, 10, 19) };
        ReportSchedule.UpgradeLegacy(settings, Zurich, Utc(2026, 10, 19, 9, 0));
        settings.TimeZoneId = "Europe/Zurich";
        var schedule = ReportSchedule.From(settings);

        Assert.IsFalse(schedule.IsDigestDue(settings, Utc(2026, 10, 19, 9, 0)));
        Assert.IsTrue(schedule.IsDigestDue(settings, Utc(2026, 10, 20, 4, 0)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LegacyUtcTime_IsTodaysUtcEquivalent()
    {
        Assert.AreEqual("04:00", ReportSchedule.LegacyUtcTime(new TimeOnly(6, 0), Zurich, Utc(2026, 10, 19, 9, 0)));
        Assert.AreEqual("05:00", ReportSchedule.LegacyUtcTime(new TimeOnly(6, 0), Zurich, Utc(2026, 12, 1, 9, 0)));
    }

    // ---- Storage: the upgrade is persisted on first read, updates validate ----

    private static RelayDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new RelayDbContext(options, new CurrentTenant());
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Service_GetAsync_UpgradesAndPersistsLegacyRow()
    {
        using var db = NewDb();
        db.ReportingSettings.Add(new ReportingSettings { Id = 1, Enabled = true, DailyTimeLocal = null, DailyTimeUtc = "04:00" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var expected = DailySchedule.Format(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(DateTime.UtcNow.Date.AddHours(4), DateTimeKind.Utc), TimeZoneInfo.Local)));

        var svc = new ReportingSettingsService(db);
        var settings = await svc.GetAsync();
        Assert.AreEqual(expected, settings.DailyTimeLocal);

        db.ChangeTracker.Clear();
        var stored = await db.ReportingSettings.AsNoTracking().SingleAsync();
        Assert.AreEqual(expected, stored.DailyTimeLocal, "the converted time is saved, not recomputed on every read");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Service_UpdateAsync_StoresLocalTimes_AndRejectsInvalidInput()
    {
        using var db = NewDb();
        var svc = new ReportingSettingsService(db);
        await svc.UpdateAsync(true, "ops@example.com", null, "06:00", "05:30", "Europe/Zurich", 10);

        var s = await svc.GetAsync();
        Assert.AreEqual("06:00", s.DailyTimeLocal);
        Assert.AreEqual("05:30", s.SelfCheckTimeLocal);
        Assert.AreEqual("Europe/Zurich", s.TimeZoneId);
        Assert.IsTrue(s.DailyTimeUtc is "04:00" or "05:00", "legacy column holds the UTC equivalent for downgrades");

        await svc.UpdateAsync(true, "ops@example.com", null, "06:00", "", null, 10);
        s = await svc.GetAsync();
        Assert.IsNull(s.SelfCheckTimeLocal, "blank = 30 minutes before the digest");
        Assert.IsNull(s.TimeZoneId, "blank = the server's zone");

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => svc.UpdateAsync(true, null, null, "25:00", null, null, 10));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => svc.UpdateAsync(true, null, null, "06:00", "x", null, 10));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => svc.UpdateAsync(true, null, null, "06:00", null, "Mars/Olympus", 10));
    }
}

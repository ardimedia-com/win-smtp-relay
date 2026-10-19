namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// Local-time schedules (T225): a run time is a wall-clock time in a zone, computed into UTC per day, so it keeps
/// its local time across the daylight-saving changes. Europe/Zurich: summer time ends 2026-10-25 (03:00 CEST →
/// 02:00 CET, 02:00–03:00 happens twice) and starts 2027-03-28 (02:00 CET → 03:00 CEST, 02:00–03:00 does not exist).
/// </summary>
[TestClass]
public class DailyScheduleTimeZoneTests
{
    private static readonly TimeOnly Fallback = new(6, 0);
    private static TimeZoneInfo Zurich => TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_AcrossAutumnChange_KeepsLocalTime()
    {
        // Saturday 24.10. 06:00 CEST = 04:00 UTC; Sunday 25.10. 06:00 CET = 05:00 UTC.
        var saturdayRun = DailySchedule.NextRunUtc("06:00", Fallback, Zurich, Utc(2026, 10, 24, 0, 0));
        Assert.AreEqual(Utc(2026, 10, 24, 4, 0), saturdayRun);

        var sundayRun = DailySchedule.NextRunUtc("06:00", Fallback, Zurich, saturdayRun);
        Assert.AreEqual(Utc(2026, 10, 25, 5, 0), sundayRun);

        Assert.AreEqual(Utc(2026, 10, 26, 5, 0), DailySchedule.NextRunUtc("06:00", Fallback, Zurich, sundayRun));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_AcrossSpringChange_KeepsLocalTime()
    {
        // Saturday 27.03.2027 06:00 CET = 05:00 UTC; Sunday 28.03. 06:00 CEST = 04:00 UTC.
        var saturdayRun = DailySchedule.NextRunUtc("06:00", Fallback, Zurich, Utc(2027, 3, 27, 0, 0));
        Assert.AreEqual(Utc(2027, 3, 27, 5, 0), saturdayRun);
        Assert.AreEqual(Utc(2027, 3, 28, 4, 0), DailySchedule.NextRunUtc("06:00", Fallback, Zurich, saturdayRun));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_TimeInSpringGap_RunsAtFirstValidInstantAfterIt()
    {
        // 02:30 does not exist on 28.03.2027 — the clock jumps from 02:00 CET to 03:00 CEST (= 01:00 UTC).
        var run = DailySchedule.NextRunUtc("02:30", Fallback, Zurich, Utc(2027, 3, 27, 2, 0));
        Assert.AreEqual(Utc(2027, 3, 28, 1, 0), run);

        // The day after, 02:30 CEST exists again (00:30 UTC) — exactly one run per day.
        Assert.AreEqual(Utc(2027, 3, 29, 0, 30), DailySchedule.NextRunUtc("02:30", Fallback, Zurich, run));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_TimeInAutumnOverlap_RunsOnceAtFirstOccurrence()
    {
        // 02:30 happens twice on 25.10.2026: 02:30 CEST (00:30 UTC) and 02:30 CET (01:30 UTC). Only the first runs.
        var first = DailySchedule.NextRunUtc("02:30", Fallback, Zurich, Utc(2026, 10, 24, 12, 0));
        Assert.AreEqual(Utc(2026, 10, 25, 0, 30), first);

        // After the first run, the second occurrence is not scheduled — the next run is the following day.
        Assert.AreEqual(Utc(2026, 10, 26, 1, 30), DailySchedule.NextRunUtc("02:30", Fallback, Zurich, first));
        // Even when waiting starts between the two occurrences.
        Assert.AreEqual(Utc(2026, 10, 26, 1, 30), DailySchedule.NextRunUtc("02:30", Fallback, Zurich, Utc(2026, 10, 25, 1, 0)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_UtcZone_IgnoresDaylightSaving()
    {
        // A server whose schedule zone is UTC: 06:00 UTC on both sides of the European change.
        Assert.AreEqual(Utc(2026, 10, 24, 6, 0), DailySchedule.NextRunUtc("06:00", Fallback, TimeZoneInfo.Utc, Utc(2026, 10, 24, 0, 0)));
        Assert.AreEqual(Utc(2026, 10, 25, 6, 0), DailySchedule.NextRunUtc("06:00", Fallback, TimeZoneInfo.Utc, Utc(2026, 10, 24, 6, 0)));
        // The UTC overload is the same as the UTC zone.
        Assert.AreEqual(Utc(2026, 10, 25, 6, 0), DailySchedule.NextRunUtc("06:00", Fallback, Utc(2026, 10, 24, 6, 0)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_ServerInUtc_ScheduleInZurich_StillLocal()
    {
        // The schedule names its zone, so the server's own zone (UTC on a cloud VM) does not matter.
        var zone = ScheduleTimeZone.Resolve("Europe/Zurich");
        Assert.AreEqual(Utc(2026, 10, 25, 5, 0), DailySchedule.NextRunUtc("06:00", Fallback, zone, Utc(2026, 10, 24, 4, 0)));
        // And a blank zone means the server's zone.
        Assert.AreEqual(TimeZoneInfo.Local.Id, ScheduleTimeZone.Resolve(null).Id);
        Assert.AreEqual(TimeZoneInfo.Local.Id, ScheduleTimeZone.Resolve("No/Such_Zone").Id);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PreviousRunUtc_ReturnsTheRunThatIsDue()
    {
        Assert.AreEqual(Utc(2026, 10, 25, 5, 0), DailySchedule.PreviousRunUtc("06:00", Fallback, Zurich, Utc(2026, 10, 25, 5, 0)));
        Assert.AreEqual(Utc(2026, 10, 24, 4, 0), DailySchedule.PreviousRunUtc("06:00", Fallback, Zurich, Utc(2026, 10, 25, 4, 59)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ScheduleTimeZone_ValidatesIanaAndWindowsIds()
    {
        Assert.IsTrue(ScheduleTimeZone.IsValid(null));
        Assert.IsTrue(ScheduleTimeZone.IsValid("Europe/Zurich"));
        Assert.IsTrue(ScheduleTimeZone.IsValid("W. Europe Standard Time"));
        Assert.IsFalse(ScheduleTimeZone.IsValid("Mars/Olympus"));
        Assert.AreEqual("Europe/Zurich", ScheduleTimeZone.DisplayId(Zurich));
    }
}

using WinSmtpRelay.Core;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class DailyScheduleTests
{
    private static readonly TimeOnly Fallback = new(5, 30);

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_BeforeTarget_ReturnsToday()
    {
        var anchor = new DateTime(2026, 9, 28, 4, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(new DateTime(2026, 9, 28, 5, 30, 0, DateTimeKind.Utc), DailySchedule.NextRunUtc("05:30", Fallback, anchor));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_AfterTarget_ReturnsTomorrow()
    {
        var anchor = new DateTime(2026, 9, 28, 6, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(new DateTime(2026, 9, 29, 5, 30, 0, DateTimeKind.Utc), DailySchedule.NextRunUtc("05:30", Fallback, anchor));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_ExactlyAtTarget_ReturnsTomorrow()
    {
        // A run that just finished at the target minute must not be scheduled again for the same minute.
        var anchor = new DateTime(2026, 9, 28, 5, 30, 0, DateTimeKind.Utc);
        Assert.AreEqual(new DateTime(2026, 9, 29, 5, 30, 0, DateTimeKind.Utc), DailySchedule.NextRunUtc("05:30", Fallback, anchor));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_Unparsable_UsesFallback()
    {
        var anchor = new DateTime(2026, 9, 28, 1, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(new DateTime(2026, 9, 28, 5, 30, 0, DateTimeKind.Utc), DailySchedule.NextRunUtc("not-a-time", Fallback, anchor));
        Assert.AreEqual(new DateTime(2026, 9, 28, 5, 30, 0, DateTimeKind.Utc), DailySchedule.NextRunUtc(null, Fallback, anchor));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NextRunUtc_AnchoredTarget_IsReachedWhenTheClockPassesIt()
    {
        // Regression (self-check stuck at the startup run, 2026-09-23 → 28): the target is fixed when the wait
        // starts, so once the clock reaches it the remaining wait is <= 0. Recomputing from "now" instead would
        // move the target to tomorrow at that very moment and the scheduled run never happened.
        var waitStarted = new DateTime(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc);
        var target = DailySchedule.NextRunUtc("05:30", Fallback, waitStarted);
        var clockAtTarget = new DateTime(2026, 9, 28, 5, 30, 0, DateTimeKind.Utc);

        Assert.IsTrue(target - clockAtTarget <= TimeSpan.Zero);
        Assert.IsTrue(DailySchedule.NextRunUtc("05:30", Fallback, clockAtTarget) - clockAtTarget > TimeSpan.Zero,
            "recomputing from the current clock never reaches the run — the old bug");
    }
}

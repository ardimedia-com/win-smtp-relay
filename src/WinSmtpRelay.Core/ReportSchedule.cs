using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core;

/// <summary>
/// The effective daily schedules from <see cref="ReportingSettings"/>: the digest time and the self-check time as
/// local times of day in one time zone. Pure — the background services, the Settings page, the API and the
/// self-check's own "reporting" finding all read the schedule through this, so they agree.
/// </summary>
public sealed record ReportSchedule(TimeOnly ReportTime, TimeOnly SelfCheckTime, bool SelfCheckFollowsReport,
    TimeZoneInfo Zone, string ZoneLabel)
{
    /// <summary>Digest time when nothing (or nothing valid) is stored.</summary>
    public static readonly TimeOnly DefaultReportTime = new(6, 0);

    /// <summary>The self-check runs this long before the digest unless a time of its own is set.</summary>
    public static readonly TimeSpan DefaultSelfCheckLead = TimeSpan.FromMinutes(30);

    public static ReportSchedule From(ReportingSettings settings)
    {
        var zone = ScheduleTimeZone.Resolve(settings.TimeZoneId);
        var report = DailySchedule.ParseOrDefault(settings.DailyTimeLocal, DefaultReportTime);
        var follows = !DailySchedule.TryParseTime(settings.SelfCheckTimeLocal, out var check);
        if (follows)
            check = DefaultSelfCheckFor(report);
        return new ReportSchedule(report, check, follows, zone, ScheduleTimeZone.DisplayId(zone));
    }

    /// <summary>The default self-check time for a digest time: 30 minutes earlier (wrapping past midnight).</summary>
    public static TimeOnly DefaultSelfCheckFor(TimeOnly reportTime) => reportTime.Add(-DefaultSelfCheckLead);

    /// <summary>"06:00 (Europe/Zurich)".</summary>
    public string Label(TimeOnly time) => $"{DailySchedule.Format(time)} ({ZoneLabel})";

    public DateTime NextReportUtc(DateTime afterUtc) =>
        DailySchedule.NextRunUtc(DailySchedule.Format(ReportTime), DefaultReportTime, Zone, afterUtc);

    public DateTime NextSelfCheckUtc(DateTime afterUtc) =>
        DailySchedule.NextRunUtc(DailySchedule.Format(SelfCheckTime), DefaultSelfCheckFor(DefaultReportTime), Zone, afterUtc);

    /// <summary>The digest run that is due at <paramref name="nowUtc"/> (the latest scheduled instant at or before it).</summary>
    public DateTime DueReportUtc(DateTime nowUtc) =>
        DailySchedule.PreviousRunUtc(DailySchedule.Format(ReportTime), DefaultReportTime, Zone, nowUtc);

    /// <summary>The local calendar date of a UTC instant in the schedule's zone.</summary>
    public DateOnly LocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));

    /// <summary>A UTC instant rendered in the schedule's zone: "2026-10-19 06:00 (Europe/Zurich)".</summary>
    public string FormatLocal(DateTimeOffset utc) =>
        $"{TimeZoneInfo.ConvertTime(utc, Zone):yyyy-MM-dd HH:mm} ({ZoneLabel})";

    /// <summary>
    /// True when the daily digest is due at <paramref name="nowUtc"/>: today's (local) digest time has passed and
    /// no digest was sent since — and none on the same local day, so moving the time later on a day that already
    /// had its digest does not send a second one. A digest missed on an earlier day (service down) is not sent
    /// late; the next one goes out at today's time, as before.
    /// </summary>
    public bool IsDigestDue(ReportingSettings settings, DateTime nowUtc)
    {
        var due = DueReportUtc(nowUtc);
        if (LocalDate(due) != LocalDate(nowUtc))
            return false;
        if (settings.LastDigestSentUtc is { } last)
            return last.UtcDateTime < due && LocalDate(last.UtcDateTime) != LocalDate(due);
        return settings.LastDigestSentDate != LocalDate(due);
    }

    /// <summary>
    /// Upgrades a row written before local-time schedules existed (<see cref="ReportingSettings.DailyTimeLocal"/>
    /// is null): the old fixed UTC digest time is converted into the local time it means TODAY in
    /// <paramref name="zone"/> (the server's zone), so the next digest goes out at the same instant as before —
    /// e.g. 04:00 UTC on a summer day becomes 06:00 Europe/Zurich, which then stays 06:00 in winter too. The last
    /// send moment is reconstructed from the old date + time. The self-check follows the digest (30 min earlier).
    /// Returns false when there was nothing to upgrade.
    /// </summary>
    public static bool UpgradeLegacy(ReportingSettings settings, TimeZoneInfo zone, DateTime nowUtc)
    {
        if (settings.DailyTimeLocal is not null)
            return false;

        var utcTime = DailySchedule.ParseOrDefault(settings.DailyTimeUtc, DefaultReportTime);
        var today = DateOnly.FromDateTime(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        var instant = DateTime.SpecifyKind(today.ToDateTime(utcTime), DateTimeKind.Utc);
        settings.DailyTimeLocal = DailySchedule.Format(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(instant, zone)));

        if (settings.LastDigestSentUtc is null && settings.LastDigestSentDate is { } lastDate)
            settings.LastDigestSentUtc = new DateTimeOffset(lastDate.ToDateTime(utcTime), TimeSpan.Zero);

        return true;
    }

    /// <summary>The UTC equivalent of a local schedule time on the given day — kept in the legacy column for downgrades.</summary>
    public static string LegacyUtcTime(TimeOnly localTime, TimeZoneInfo zone, DateTime nowUtc)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone));
        return DailySchedule.Format(TimeOnly.FromDateTime(DailySchedule.OccurrenceUtc(localDate, localTime, zone)));
    }
}

using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class ReportingSettingsService(RelayDbContext db) : IReportingSettingsService
{
    public async Task<ReportingSettings> GetAsync(CancellationToken ct = default)
    {
        var settings = await db.ReportingSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
            return new ReportingSettings();

        // A row from before local-time schedules: convert its fixed UTC digest time into the server's local time
        // once (so the next digest keeps its instant) and persist it — converting on every read would shift the
        // time by an hour at the next DST change.
        if (ReportSchedule.UpgradeLegacy(settings, ScheduleTimeZone.Resolve(settings.TimeZoneId), DateTime.UtcNow))
            await db.SaveChangesAsync(ct);

        db.Entry(settings).State = EntityState.Detached;
        return settings;
    }

    public async Task UpdateAsync(bool enabled, string? recipientAddress, string? fromAddress, string dailyTimeLocal,
        string? selfCheckTimeLocal, string? timeZoneId, int bounceRateAlertPercent, CancellationToken ct = default)
    {
        var report = string.IsNullOrWhiteSpace(dailyTimeLocal) ? ReportSchedule.DefaultReportTime
            : DailySchedule.TryParseTime(dailyTimeLocal, out var r) ? r
            : throw new ArgumentException($"'{dailyTimeLocal}' is not a valid time (HH:mm).", nameof(dailyTimeLocal));
        TimeOnly? check = string.IsNullOrWhiteSpace(selfCheckTimeLocal) ? null
            : DailySchedule.TryParseTime(selfCheckTimeLocal, out var c) ? c
            : throw new ArgumentException($"'{selfCheckTimeLocal}' is not a valid time (HH:mm).", nameof(selfCheckTimeLocal));
        if (!ScheduleTimeZone.IsValid(timeZoneId))
            throw new ArgumentException($"'{timeZoneId}' is not a known time zone (use an IANA id such as Europe/Zurich, or leave it blank for the server's zone).", nameof(timeZoneId));

        var settings = await Row(ct);
        settings.Enabled = enabled;
        settings.RecipientAddress = Blank(recipientAddress);
        settings.FromAddress = Blank(fromAddress);
        settings.TimeZoneId = Blank(timeZoneId);
        settings.DailyTimeLocal = DailySchedule.Format(report);
        settings.SelfCheckTimeLocal = check is { } t ? DailySchedule.Format(t) : null;
        settings.DailyTimeUtc = ReportSchedule.LegacyUtcTime(report, ScheduleTimeZone.Resolve(settings.TimeZoneId), DateTime.UtcNow);
        settings.BounceRateAlertPercent = Math.Clamp(bounceRateAlertPercent, 0, 100);
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkDigestSentAsync(DateTimeOffset sentUtc, DateOnly localDate, CancellationToken ct = default)
    {
        var settings = await Row(ct);
        settings.LastDigestSentUtc = sentUtc;
        settings.LastDigestSentDate = localDate;
        await db.SaveChangesAsync(ct);
    }

    private async Task<ReportingSettings> Row(CancellationToken ct)
    {
        var settings = await db.ReportingSettings.FindAsync([1], ct);
        if (settings is null)
        {
            settings = new ReportingSettings { Id = 1 };
            db.ReportingSettings.Add(settings);
        }
        else
        {
            ReportSchedule.UpgradeLegacy(settings, ScheduleTimeZone.Resolve(settings.TimeZoneId), DateTime.UtcNow);
        }
        return settings;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

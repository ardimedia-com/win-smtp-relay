using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IReportingSettingsService
{
    /// <summary>
    /// The settings row (defaults when none exists). A row from before local-time schedules is upgraded and saved
    /// on first read (<see cref="ReportSchedule.UpgradeLegacy"/>).
    /// </summary>
    Task<ReportingSettings> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves the reporting settings. <paramref name="dailyTimeLocal"/> and <paramref name="selfCheckTimeLocal"/> are
    /// local times (HH:mm) in <paramref name="timeZoneId"/> (blank = the server's zone); a blank self-check time
    /// means "30 minutes before the digest". Throws <see cref="ArgumentException"/> for an invalid time or zone.
    /// </summary>
    Task UpdateAsync(bool enabled, string? recipientAddress, string? fromAddress, string dailyTimeLocal,
        string? selfCheckTimeLocal, string? timeZoneId, int bounceRateAlertPercent, CancellationToken ct = default);

    /// <summary>Records that the daily digest was sent at <paramref name="sentUtc"/> for the local day <paramref name="localDate"/>.</summary>
    Task MarkDigestSentAsync(DateTimeOffset sentUtc, DateOnly localDate, CancellationToken ct = default);
}

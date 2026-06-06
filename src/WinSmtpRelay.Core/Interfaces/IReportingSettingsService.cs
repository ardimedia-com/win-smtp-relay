using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IReportingSettingsService
{
    Task<ReportingSettings> GetAsync(CancellationToken ct = default);

    Task UpdateAsync(bool enabled, string? recipientAddress, string? fromAddress, string dailyTimeUtc,
        int bounceRateAlertPercent, CancellationToken ct = default);

    /// <summary>Records that the daily digest was sent on <paramref name="date"/> (UTC).</summary>
    Task MarkDigestSentAsync(DateOnly date, CancellationToken ct = default);
}

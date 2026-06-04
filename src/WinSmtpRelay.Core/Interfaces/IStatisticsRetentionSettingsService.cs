using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IStatisticsRetentionSettingsService
{
    Task<StatisticsRetentionSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Updates the statistics retention days and daily aggregation time (HH:mm UTC).</summary>
    Task UpdateAsync(int retentionDays, string aggregationTimeUtc, CancellationToken ct = default);
}

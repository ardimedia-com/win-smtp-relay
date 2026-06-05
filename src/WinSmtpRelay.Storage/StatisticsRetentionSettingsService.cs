using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class StatisticsRetentionSettingsService(RelayDbContext db) : IStatisticsRetentionSettingsService
{
    public async Task<StatisticsRetentionSettings> GetAsync(CancellationToken ct = default)
        => await db.StatisticsRetentionSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new StatisticsRetentionSettings();

    public async Task UpdateAsync(int retentionDays, string aggregationTimeUtc, CancellationToken ct = default)
    {
        var settings = await db.StatisticsRetentionSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new StatisticsRetentionSettings { Id = 1 };
            db.StatisticsRetentionSettings.Add(settings);
        }

        settings.RetentionDays = Math.Max(1, retentionDays);
        settings.AggregationTimeUtc = string.IsNullOrWhiteSpace(aggregationTimeUtc) ? "00:00" : aggregationTimeUtc.Trim();
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        // No cache: consumed only by the daily background aggregator, which reads it each cycle.
    }
}

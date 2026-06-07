using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class DataRetentionSettingsService(RelayDbContext db) : IDataRetentionSettingsService
{
    public async Task<DataRetentionSettings> GetAsync(CancellationToken ct = default)
        => await db.DataRetentionSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new DataRetentionSettings();

    public async Task UpdateAsync(DataRetentionSettings input, CancellationToken ct = default)
    {
        var settings = await db.DataRetentionSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new DataRetentionSettings { Id = 1 };
            db.DataRetentionSettings.Add(settings);
        }

        settings.Profile = string.IsNullOrWhiteSpace(input.Profile) ? RetentionProfiles.Custom : input.Profile.Trim();
        settings.StripBodyOnDelivery = input.StripBodyOnDelivery;
        settings.MessageHistoryDays = Math.Max(1, input.MessageHistoryDays);
        // Hard floor on the audit log: delivery evidence can never be retained for less than this, even by
        // a configuration change (secure-by-default).
        settings.DeliveryLogDays = Math.Max(DataRetentionSettings.DeliveryLogFloorDays, input.DeliveryLogDays);
        settings.SuppressionDays = Math.Max(0, input.SuppressionDays);
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        // No cache: consumed by the nightly maintenance task (each cycle) and once per delivered message.
    }
}

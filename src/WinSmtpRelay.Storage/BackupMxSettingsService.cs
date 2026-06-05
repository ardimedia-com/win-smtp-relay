using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class BackupMxSettingsService(RelayDbContext db, IRuntimeConfigCache cache) : IBackupMxSettingsService
{
    public async Task<BackupMxSettings> GetAsync(CancellationToken ct = default)
        => await db.BackupMxSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new BackupMxSettings();

    public async Task UpdateAsync(bool enabled, string? domains, int retryIntervalMinutes, int maxHoldHours, CancellationToken ct = default)
    {
        var settings = await db.BackupMxSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new BackupMxSettings { Id = 1 };
            db.BackupMxSettings.Add(settings);
        }

        settings.Enabled = enabled;
        settings.Domains = domains?.Trim() ?? "";
        settings.RetryIntervalMinutes = Math.Max(1, retryIntervalMinutes);
        settings.MaxHoldHours = Math.Max(1, maxHoldHours);
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // The SMTP/delivery hot path caches these — refresh so the change takes effect now.
        cache.Invalidate();
    }
}

using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IBackupMxSettingsService
{
    Task<BackupMxSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Updates backup-MX settings and refreshes the SMTP/delivery hot-path cache. <paramref name="domains"/> is a delimited list.</summary>
    Task UpdateAsync(bool enabled, string? domains, int retryIntervalMinutes, int maxHoldHours, CancellationToken ct = default);
}

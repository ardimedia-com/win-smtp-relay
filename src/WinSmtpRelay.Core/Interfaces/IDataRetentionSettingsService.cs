using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IDataRetentionSettingsService
{
    Task<DataRetentionSettings> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists the data-retention settings. The delivery-log retention is floored at
    /// <see cref="DataRetentionSettings.DeliveryLogFloorDays"/> so delivery evidence cannot be scrubbed.
    /// </summary>
    Task UpdateAsync(DataRetentionSettings settings, CancellationToken ct = default);
}

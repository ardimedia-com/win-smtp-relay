namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Host-level data-retention settings (single row), runtime-editable. Controls how long each category of
/// accumulating data is kept before the nightly maintenance run purges it. Seeded once from appsettings
/// <c>DataRetention</c>, then authoritative. A <see cref="Profile"/> is a named preset that fills the
/// per-category values (see <see cref="RetentionProfiles"/>); editing any value individually makes it
/// "Custom". Statistics retention is intentionally NOT stored here — it lives in
/// <see cref="StatisticsRetentionSettings"/> alongside the maintenance schedule — but the Data-retention
/// UI surfaces and edits it together with these values.
/// </summary>
public class DataRetentionSettings
{
    /// <summary>
    /// Minimum retention enforced for the delivery audit log so delivery evidence can never be scrubbed to
    /// near-zero by a configuration change (secure-by-default). Enforced in the settings service and again
    /// in the purge as defence in depth.
    /// </summary>
    public const int DeliveryLogFloorDays = 7;

    public int Id { get; set; }

    /// <summary>Named preset last applied (display only): Standard, PrivacyFirst, Financial, Healthcare, Custom.</summary>
    public string Profile { get; set; } = "Standard";

    /// <summary>
    /// Drop the message body (<c>RawMessage</c>) immediately once a message is delivered, keeping only its
    /// metadata. Off for archive/regulated profiles that must retain the communication content.
    /// </summary>
    public bool StripBodyOnDelivery { get; set; } = true;

    /// <summary>Days to keep terminal queued messages (delivered/bounced/failed/suppressed) before the row is purged.</summary>
    public int MessageHistoryDays { get; set; } = 30;

    /// <summary>Days to keep the per-recipient delivery audit log (Journal). Floored at <see cref="DeliveryLogFloorDays"/>.</summary>
    public int DeliveryLogDays { get; set; } = 90;

    /// <summary>Days to keep suppression-list entries; 0 = keep indefinitely (recommended).</summary>
    public int SuppressionDays { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

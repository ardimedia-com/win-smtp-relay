namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Best-practice data-retention presets ("templates") that populate the per-category retention values.
/// They are starting points, NOT legal advice — the actual obligation depends on the operator's
/// jurisdiction and business, and these regimes apply to the operator/customer, not to the relay itself.
/// The relay is also not a certified WORM/tamper-proof archive, so the regulated profiles set sensible
/// retention <em>durations</em> but do not by themselves satisfy MiFID II / SEC 17a-4 archival rules.
/// </summary>
public static class RetentionProfiles
{
    /// <summary>Sentinel profile name used when the per-category values don't match any preset.</summary>
    public const string Custom = "Custom";

    /// <summary>One retention preset. <see cref="StatisticsDays"/> is applied to the statistics retention setting.</summary>
    /// <param name="Key">Stable identifier persisted in <see cref="DataRetentionSettings.Profile"/>.</param>
    /// <param name="Name">Human-readable label for the selector.</param>
    /// <param name="StripBodyOnDelivery">Whether to drop the message body on delivery (off = archive content).</param>
    /// <param name="MessageHistoryDays">Retention for terminal queued messages.</param>
    /// <param name="DeliveryLogDays">Retention for the delivery audit log.</param>
    /// <param name="StatisticsDays">Retention for daily statistics aggregates.</param>
    /// <param name="SuppressionDays">Retention for suppression entries (0 = keep forever).</param>
    /// <param name="Basis">Short note on the regulatory basis / intent, shown in the UI.</param>
    public record Profile(
        string Key,
        string Name,
        bool StripBodyOnDelivery,
        int MessageHistoryDays,
        int DeliveryLogDays,
        int StatisticsDays,
        int SuppressionDays,
        string Basis);

    private const int Year = 365;

    public static readonly IReadOnlyList<Profile> All =
    [
        new("Standard", "Standard (GDPR-balanced)",
            StripBodyOnDelivery: true, MessageHistoryDays: 30, DeliveryLogDays: 90, StatisticsDays: 365, SuppressionDays: 0,
            "Balanced GDPR / Swiss-FADP data-minimisation defaults. Recommended for most deployments."),
        new("PrivacyFirst", "Privacy-first (minimal)",
            StripBodyOnDelivery: true, MessageHistoryDays: 7, DeliveryLogDays: 30, StatisticsDays: 90, SuppressionDays: 0,
            "Aggressive data-minimisation: short windows, message bodies stripped on delivery."),
        new("Financial", "Financial (MiFID II / SEC-FINRA)",
            StripBodyOnDelivery: false, MessageHistoryDays: 7 * Year, DeliveryLogDays: 7 * Year, StatisticsDays: 7 * Year, SuppressionDays: 0,
            "Retains communication content long-term. MiFID II: 5 yr (extendable to 7 on regulator request); "
            + "SEC 17a-4 / FINRA: 3 yr minimum (firms often keep longer). NOT a certified WORM archive."),
        new("Healthcare", "Healthcare (HIPAA)",
            StripBodyOnDelivery: false, MessageHistoryDays: 6 * Year, DeliveryLogDays: 6 * Year, StatisticsDays: 7 * Year, SuppressionDays: 0,
            "HIPAA: 6-yr retention of compliance documentation; clinical 'designated record set' emails may "
            + "need longer under state medical-record law. Verify your jurisdiction."),
    ];

    public static Profile? Find(string key) => All.FirstOrDefault(p => p.Key == key);
}

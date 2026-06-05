namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Host-level statistics retention/aggregation settings (single row), runtime-editable.
/// Seeded once from appsettings <c>Statistics</c>, then authoritative. Consumed by the daily
/// background aggregator (not on the SMTP hot path), so it is read directly without caching.
/// </summary>
public class StatisticsRetentionSettings
{
    public int Id { get; set; }

    /// <summary>How many days of daily statistics to keep before purging.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>UTC time-of-day (HH:mm) at which the daily aggregation/purge runs.</summary>
    public string AggregationTimeUtc { get; set; } = "00:00";

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

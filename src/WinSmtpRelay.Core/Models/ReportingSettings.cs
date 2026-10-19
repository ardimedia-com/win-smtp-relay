namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Host-level email-reporting settings (single row), runtime-editable. Drives the background
/// reporting service: a daily activity digest and immediate alerts on important incidents
/// (a sending IP getting blocklisted, an elevated bounce rate), and the time of the daily self-check.
/// Read directly by the background services each cycle (not on the SMTP hot path), so it is not cached.
/// <para>
/// Schedules are LOCAL times of day in <see cref="TimeZoneId"/> (blank = the server's time zone), so they keep
/// their wall-clock time across daylight-saving changes; every next run is computed in UTC from them
/// (<see cref="DailySchedule"/>).
/// </para>
/// </summary>
public class ReportingSettings
{
    public int Id { get; set; }

    /// <summary>Master switch for daily digests and incident alerts.</summary>
    public bool Enabled { get; set; }

    /// <summary>Where reports/alerts are sent. Required for any mail to be sent.</summary>
    public string? RecipientAddress { get; set; }

    /// <summary>From-address for reports. Falls back to the signup from-address when blank.</summary>
    public string? FromAddress { get; set; }

    /// <summary>
    /// Local time of day (HH:mm, in <see cref="TimeZoneId"/>) at which the daily digest is sent. Null only on a
    /// row written by a version before local-time schedules; <see cref="ReportSchedule.UpgradeLegacy"/> fills it
    /// from <see cref="DailyTimeUtc"/> on first read.
    /// </summary>
    public string? DailyTimeLocal { get; set; } = "06:00";

    /// <summary>
    /// Local time of day (HH:mm, in <see cref="TimeZoneId"/>) of the daily self-check. Null = 30 minutes before
    /// the digest, so the digest always carries a fresh result.
    /// </summary>
    public string? SelfCheckTimeLocal { get; set; }

    /// <summary>IANA time-zone id of the schedules (e.g. <c>Europe/Zurich</c>). Null/blank = the server's time zone.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// Legacy: the digest time as a fixed UTC time of day (HH:mm), the only schedule before local-time schedules.
    /// Still written (the UTC equivalent of <see cref="DailyTimeLocal"/> at the time of saving) so a downgrade to
    /// an older version keeps a sensible digest time; not read for scheduling any more.
    /// </summary>
    public string DailyTimeUtc { get; set; } = "06:00";

    /// <summary>Bounce-rate (%) over the last 24h that triggers an immediate alert; 0 disables it.</summary>
    public int BounceRateAlertPercent { get; set; } = 10;

    /// <summary>
    /// The local date (in <see cref="TimeZoneId"/>) the last daily digest was sent, so it is sent at most once per
    /// day. Rows from before local-time schedules hold the UTC date.
    /// </summary>
    public DateOnly? LastDigestSentDate { get; set; }

    /// <summary>The instant the last daily digest was sent; start of the next digest's "since last report" window.</summary>
    public DateTimeOffset? LastDigestSentUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

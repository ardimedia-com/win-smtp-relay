namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Host-level email-reporting settings (single row), runtime-editable. Drives the background
/// reporting service: a daily activity digest and immediate alerts on important incidents
/// (a sending IP getting blocklisted, an elevated bounce rate). Read directly by the background
/// service each cycle (not on the SMTP hot path), so it is not cached.
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

    /// <summary>UTC time-of-day (HH:mm) at which the daily digest is sent.</summary>
    public string DailyTimeUtc { get; set; } = "06:00";

    /// <summary>Bounce-rate (%) over the last 24h that triggers an immediate alert; 0 disables it.</summary>
    public int BounceRateAlertPercent { get; set; } = 10;

    /// <summary>The date (UTC) the last daily digest was sent, so it is sent at most once per day.</summary>
    public DateOnly? LastDigestSentDate { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

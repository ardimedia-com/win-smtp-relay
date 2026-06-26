namespace WinSmtpRelay.Core.Configuration;

/// <summary>
/// Operational tuning for the daily self-check (schedule, retention, probe targets, and the thresholds that
/// turn a measurement into a Warning/Error). Bound from the <c>HealthCheck</c> appsettings section. Following
/// the framework-convention rule, these tuning knobs live in appsettings rather than a DB settings table; the
/// alert recipient/from-address and enabled-state are reused from <c>Reporting</c> settings.
/// </summary>
public class HealthCheckOptions
{
    public const string SectionName = "HealthCheck";

    /// <summary>Master switch for the daily run and the page's data. Default on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>UTC time of day to run the self-check (HH:mm). Defaults before the digest time so the digest carries fresh results.</summary>
    public string DailyTimeUtc { get; set; } = "05:30";

    /// <summary>How many days of snapshots to keep for the history view.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Minimum gap between immediate "new blocking finding" alert emails, to avoid spamming.</summary>
    public int AlertCooldownHours { get; set; } = 6;

    /// <summary>How long any single check may run before it is abandoned (a network probe that hangs).</summary>
    public int ProbeTimeoutSeconds { get; set; } = 12;

    // ---- Connectivity ----

    /// <summary>Domain whose MX is resolved and connected to on port 25 to prove outbound :25 is not blocked.</summary>
    public string Outbound25TestDomain { get; set; } = "gmail.com";

    // ---- Queue ----

    /// <summary>Oldest still-queued message older than this (minutes) is a Warning — the queue is draining slowly.</summary>
    public int OldestPendingWarningMinutes { get; set; } = 60;

    /// <summary>Oldest still-queued message older than this (hours) is an Error — the queue is stuck.</summary>
    public int OldestPendingErrorHours { get; set; } = 6;

    /// <summary>This many or more queued messages with a high retry count is a Warning (systematic delivery problem).</summary>
    public int RetryBacklogWarning { get; set; } = 25;

    /// <summary>A queued message at/above this retry count counts toward the retry backlog.</summary>
    public int HighRetryCount { get; set; } = 5;

    /// <summary>24h bounce rate (percent) at/above this is a Warning.</summary>
    public int BounceRateWarningPercent { get; set; } = 10;

    /// <summary>24h bounce rate (percent) at/above this is an Error.</summary>
    public int BounceRateErrorPercent { get; set; } = 30;

    /// <summary>Minimum 24h delivery attempts before the bounce-rate finding is meaningful.</summary>
    public int BounceRateMinAttempts { get; set; } = 20;

    /// <summary>This many or more new suppression-list entries in 24h is a Warning (a domain/list went bad).</summary>
    public int SuppressionSpikeWarning { get; set; } = 50;

    /// <summary>Free disk space below this (MB) on the database volume is a Warning.</summary>
    public int LowDiskWarningMb { get; set; } = 1024;

    /// <summary>Free disk space below this (MB) on the database volume is an Error.</summary>
    public int LowDiskErrorMb { get; set; } = 256;

    // ---- Certificates ----

    /// <summary>A certificate expiring within this many days is a Warning.</summary>
    public int CertExpiryWarningDays { get; set; } = 14;

    /// <summary>A certificate expiring within this many days is an Error.</summary>
    public int CertExpiryErrorDays { get; set; } = 3;

    // ---- Runtime ----

    /// <summary>Clock offset (seconds) versus the reference NTP server above which a Warning is raised (DKIM/TLS are time-sensitive).</summary>
    public int ClockSkewWarningSeconds { get; set; } = 60;

    /// <summary>Reference NTP host for the clock-skew check. Empty disables the check.</summary>
    public string NtpHost { get; set; } = "pool.ntp.org";
}

using WinSmtpRelay.Core.Health;

namespace WinSmtpRelay.Core.Models;

/// <summary>
/// One run of the daily self-check: a host-level diagnostic of setup correctness, deliverability, and the
/// message journal. NOT tenant-owned — it is a whole-relay health record, queried in host scope. Each run
/// persists its <see cref="Findings"/> plus per-severity counts so the admin UI and the daily digest can
/// render the latest result and a short history without re-running the (network-touching) checks.
/// </summary>
public class HealthCheckSnapshot
{
    public long Id { get; set; }

    /// <summary>When the run completed (UTC).</summary>
    public DateTimeOffset RunUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>How long the whole run took, for diagnosing slow checks.</summary>
    public int DurationMs { get; set; }

    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public int OkCount { get; set; }

    public List<HealthCheckFinding> Findings { get; set; } = [];

    /// <summary>The worst severity in this run — the snapshot's overall status.</summary>
    public HealthSeverity OverallSeverity =>
        ErrorCount > 0 ? HealthSeverity.Error
        : WarningCount > 0 ? HealthSeverity.Warning
        : InfoCount > 0 ? HealthSeverity.Info
        : HealthSeverity.Ok;

    /// <summary>Findings that need attention (Warning or Error), for the digest and alert summaries.</summary>
    public int IssueCount => ErrorCount + WarningCount;
}

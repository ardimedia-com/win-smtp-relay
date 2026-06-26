using WinSmtpRelay.Core.Health;

namespace WinSmtpRelay.Core.Models;

/// <summary>
/// One persisted self-check finding belonging to a <see cref="HealthCheckSnapshot"/>. <see cref="Code"/>
/// plus <see cref="Target"/> identify the finding across runs (used to detect a NEW problem worth an
/// immediate alert), while <see cref="Title"/>/<see cref="Detail"/>/<see cref="Hint"/> are the
/// human-readable explanation shown in the admin UI and the digest.
/// </summary>
public class HealthCheckFinding
{
    public long Id { get; set; }

    public long SnapshotId { get; set; }

    /// <summary>One of <see cref="HealthCategories"/>.</summary>
    public string Category { get; set; } = "";

    /// <summary>Stable machine code for this kind of finding (e.g. "outbound-port-25", "dkim-drift").</summary>
    public string Code { get; set; } = "";

    public HealthSeverity Severity { get; set; }

    /// <summary>Short headline shown as the finding's title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Full explanation of what was checked and what was found.</summary>
    public string Detail { get; set; } = "";

    /// <summary>What the finding is about (a domain, IP, port, certificate), if applicable.</summary>
    public string? Target { get; set; }

    /// <summary>Optional remediation hint ("Publish this SPF record…", "Update sending IPs…").</summary>
    public string? Hint { get; set; }
}

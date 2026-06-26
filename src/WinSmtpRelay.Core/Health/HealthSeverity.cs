namespace WinSmtpRelay.Core.Health;

/// <summary>
/// Severity of a single self-check finding. Ordered worst-last so that <c>OrderByDescending(Severity)</c>
/// surfaces the most serious findings first, and the overall snapshot status is the maximum severity.
/// </summary>
public enum HealthSeverity
{
    /// <summary>The item is configured correctly / nothing to do.</summary>
    Ok = 0,

    /// <summary>Informational — worth knowing, not a problem (e.g. a feature is deliberately off).</summary>
    Info = 1,

    /// <summary>Works today but is risky or will break soon (e.g. a certificate expiring, a slow queue).</summary>
    Warning = 2,

    /// <summary>Blocks mail delivery or is a security problem and needs attention now.</summary>
    Error = 3,
}

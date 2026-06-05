using System.ComponentModel.DataAnnotations.Schema;

namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Host-level backup-MX configuration (single row), runtime-editable. Seeded once from
/// appsettings <c>BackupMx</c>, then authoritative.
/// </summary>
public class BackupMxSettings
{
    public int Id { get; set; }

    /// <summary>Accept and hold mail for the configured domains when the primary is unreachable.</summary>
    public bool Enabled { get; set; }

    /// <summary>Backup-MX domains, stored as a semicolon/comma-delimited string.</summary>
    public string Domains { get; set; } = "";

    /// <summary>Retry interval (minutes) while holding backup-MX mail.</summary>
    public int RetryIntervalMinutes { get; set; } = 15;

    /// <summary>Maximum time (hours) to hold backup-MX mail before giving up.</summary>
    public int MaxHoldHours { get; set; } = 168;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Parsed view of <see cref="Domains"/>.</summary>
    [NotMapped]
    public IReadOnlyList<string> DomainList =>
        string.IsNullOrWhiteSpace(Domains)
            ? []
            : Domains.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

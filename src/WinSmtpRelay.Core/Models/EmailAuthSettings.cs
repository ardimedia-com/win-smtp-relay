using WinSmtpRelay.Core.Configuration;

namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Host-level inbound email-authentication policy (single row), runtime-editable.
/// Seeded once from appsettings <c>EmailAuthentication</c>, then authoritative.
/// </summary>
public class EmailAuthSettings
{
    public int Id { get; set; }

    /// <summary>Run SPF checks on inbound mail.</summary>
    public bool SpfEnabled { get; set; }

    /// <summary>Run DMARC checks on inbound mail.</summary>
    public bool DmarcEnabled { get; set; }

    /// <summary>What to do on an authentication failure: log only, reject, or quarantine.</summary>
    public EnforcementMode Enforcement { get; set; } = EnforcementMode.LogOnly;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

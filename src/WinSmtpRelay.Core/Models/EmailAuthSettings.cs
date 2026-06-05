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

    /// <summary>
    /// When true, a configured accepted <em>sender</em> domain must have its ownership verified
    /// (DNS TXT) before the relay will accept mail from it. Only applies among configured sender
    /// domains; an empty sender-domain list still means "accept all" (nothing to verify).
    /// </summary>
    public bool RequireSenderDomainVerification { get; set; }

    /// <summary>
    /// When true, a configured accepted <em>recipient</em> domain must have its ownership verified
    /// (DNS TXT) before the relay will accept mail for it. Only applies among configured recipient
    /// domains; backup-MX domains are unaffected.
    /// </summary>
    public bool RequireRecipientDomainVerification { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

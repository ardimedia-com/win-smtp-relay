namespace WinSmtpRelay.Core.Models;

/// <summary>Why a recipient address is on a tenant's suppression list.</summary>
public enum SuppressionReason
{
    /// <summary>A permanent (5xx) delivery failure to this address.</summary>
    HardBounce = 0,
    /// <summary>The recipient reported the mail as spam (feedback loop / complaint).</summary>
    Complaint = 1,
    /// <summary>Added manually by an administrator.</summary>
    Manual = 2
}

/// <summary>
/// A recipient address the relay must not deliver to for a given tenant. Populated automatically from
/// permanent bounces and complaints (and manually by admins). Repeatedly mailing dead addresses or
/// complainers is a primary cause of blocklisting, so delivery to a suppressed address is skipped.
/// </summary>
public class SuppressionEntry : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;

    /// <summary>The recipient email address, normalised to lower-case.</summary>
    public required string Address { get; set; }

    public SuppressionReason Reason { get; set; }

    /// <summary>Optional context, e.g. the SMTP status code/message that caused the suppression.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

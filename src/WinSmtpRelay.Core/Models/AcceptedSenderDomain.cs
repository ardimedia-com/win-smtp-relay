namespace WinSmtpRelay.Core.Models;

public class AcceptedSenderDomain : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public required string Domain { get; set; }

    /// <summary>Random token the tenant must publish as a DNS TXT record to prove domain ownership.</summary>
    public string VerificationToken { get; set; } = "";

    /// <summary>When ownership was last verified via DNS, or null if not yet verified.</summary>
    public DateTime? VerifiedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

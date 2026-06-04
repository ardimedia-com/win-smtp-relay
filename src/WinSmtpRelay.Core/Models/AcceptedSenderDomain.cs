namespace WinSmtpRelay.Core.Models;

public class AcceptedSenderDomain : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public required string Domain { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

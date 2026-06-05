namespace WinSmtpRelay.Core.Models;

public class AcceptedDomain : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public required string Domain { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

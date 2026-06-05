namespace WinSmtpRelay.Core.Models;

public class DkimDomain : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public required string Domain { get; set; }
    public required string Selector { get; set; }
    public required string PrivateKeyPath { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

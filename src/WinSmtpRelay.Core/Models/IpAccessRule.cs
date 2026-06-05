namespace WinSmtpRelay.Core.Models;

public class IpAccessRule : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public required string Network { get; set; }
    public IpAccessAction Action { get; set; } = IpAccessAction.Allow;
    public int SortOrder { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum IpAccessAction
{
    Allow,
    Deny
}

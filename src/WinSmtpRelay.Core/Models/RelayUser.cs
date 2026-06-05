namespace WinSmtpRelay.Core.Models;

public class RelayUser : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? AllowedSenderAddresses { get; set; }
    public int? RateLimitPerMinute { get; set; }
    public int? RateLimitPerDay { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

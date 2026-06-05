namespace WinSmtpRelay.Core.Models;

public class DeliveryLog : ITenantOwned
{
    public long Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public long QueuedMessageId { get; set; }
    public required string Recipient { get; set; }
    public required string StatusCode { get; set; }
    public required string StatusMessage { get; set; }
    public string? RemoteServer { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

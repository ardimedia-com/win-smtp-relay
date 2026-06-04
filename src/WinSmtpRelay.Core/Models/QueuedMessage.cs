namespace WinSmtpRelay.Core.Models;

public class QueuedMessage : ITenantOwned
{
    public long Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public required string MessageId { get; set; }
    public required string Sender { get; set; }
    public required string Recipients { get; set; }
    public required byte[] RawMessage { get; set; }
    public int SizeBytes { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.Queued;
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? NextRetryUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? SourceIp { get; set; }
    public string? AuthenticatedUser { get; set; }
}

public enum MessageStatus
{
    Queued = 0,
    Delivering = 1,
    Delivered = 2,
    Failed = 3,
    Bounced = 4
}

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
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextRetryUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? SourceIp { get; set; }
    public string? AuthenticatedUser { get; set; }

    /// <summary>
    /// Recipients (";"-delimited) that have already received a 250 on a previous attempt of this
    /// message. On retry the delivery skips them, so a multi-recipient/multi-domain message where one
    /// domain was temporarily down is never re-delivered to the recipients that already succeeded.
    /// </summary>
    public string? DeliveredRecipients { get; set; }
}

public enum MessageStatus
{
    Queued = 0,
    Delivering = 1,
    Delivered = 2,
    Failed = 3,
    Bounced = 4,
    /// <summary>Not sent because every recipient is on the tenant's suppression list. Terminal — not retried.</summary>
    Suppressed = 5
}

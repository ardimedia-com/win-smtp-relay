namespace WinSmtpRelay.Core.Models;

public class DeliveryLog : ITenantOwned
{
    public long Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public long QueuedMessageId { get; set; }

    /// <summary>
    /// The message's envelope sender, denormalized from the queued message so the journal stays a
    /// self-contained audit trail after the message itself is purged. Null on rows written before
    /// the column existed (when no matching message remained to backfill from).
    /// </summary>
    public string? Sender { get; set; }

    public required string Recipient { get; set; }
    public required string StatusCode { get; set; }
    public required string StatusMessage { get; set; }
    public string? RemoteServer { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

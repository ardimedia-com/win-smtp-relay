using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// Chain-of-responsibility message filter. Each filter can inspect/modify
/// the message before delivery. Return false to reject the message.
public interface IMessageFilter
{
    int Order { get; }
    Task<MessageFilterResult> FilterAsync(MessageFilterContext context, CancellationToken cancellationToken = default);
}

public class MessageFilterContext
{
    public required byte[] RawMessage { get; set; }
    public required string Sender { get; set; }
    public required string Recipients { get; set; }
    public string? SourceIp { get; set; }

    /// <summary>The owning tenant of the message being filtered — filters must apply only this
    /// tenant's rules (rewrite rules are tenant-owned and the cache loads them across all tenants).</summary>
    public int TenantId { get; set; }
}

public class MessageFilterResult
{
    public bool Accept { get; init; } = true;
    public string? RejectReason { get; init; }
    public byte[]? ModifiedRawMessage { get; init; }

    public static MessageFilterResult Accepted() => new();
    public static MessageFilterResult AcceptedWithModification(byte[] modified) => new() { ModifiedRawMessage = modified };
    public static MessageFilterResult Rejected(string reason) => new() { Accept = false, RejectReason = reason };
}

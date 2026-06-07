namespace WinSmtpRelay.Core.Models;

/// <summary>
/// A live-activity event published in-process by the relay backend and consumed directly by the
/// server-rendered Blazor admin pages — no server-to-self SignalR connection (which can't carry the
/// signed-in user's auth / tenant) is involved.
/// </summary>
public sealed record ActivityEvent(
    ActivityKind Kind,
    int? TenantId,                       // owning tenant; null = not tenant-scoped (visible to everyone)
    DateTimeOffset TimestampUtc,
    string? MessageId = null,
    string? Sender = null,
    string? Recipients = null,
    int? SizeBytes = null,
    string? Recipient = null,
    string? StatusCode = null,
    string? RemoteServer = null,
    string? SourceIp = null,
    string? EventType = null);

public enum ActivityKind
{
    /// <summary>An application submitted a message to the relay.</summary>
    MessageReceived,

    /// <summary>The relay attempted to deliver a message to a recipient.</summary>
    DeliveryAttempt,

    /// <summary>A client connected to / disconnected from the SMTP listener.</summary>
    SmtpConnection,

    /// <summary>The mail queue changed (a hint to refresh queue/depth views).</summary>
    QueueChanged,
}

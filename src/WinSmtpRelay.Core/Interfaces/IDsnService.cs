using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Generates delivery status notifications (RFC 3464 bounces) for messages that finally failed or were suppressed
/// (T1074). A DSN goes only to an envelope sender in one of the message tenant's own domains — never to a forged
/// sender (backscatter), never for null-sender mail, system mail or another DSN, never to a suppressed sender, and
/// only while the tenant has DSNs enabled. It is queued through the normal pipeline with a null return path.
/// </summary>
public interface IDsnService
{
    /// <summary>
    /// Queues a DSN for <paramref name="recipients"/> of <paramref name="message"/> when the policy allows one.
    /// Returns true when a DSN was queued.
    /// </summary>
    Task<bool> NotifyAsync(QueuedMessage message, IReadOnlyList<DsnRecipient> recipients, CancellationToken ct = default);
}

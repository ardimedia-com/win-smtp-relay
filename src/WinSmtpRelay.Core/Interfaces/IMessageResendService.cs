namespace WinSmtpRelay.Core.Interfaces;

/// <summary>Outcome of a resend attempt: the new queue id on success, or a user-facing error.</summary>
public readonly record struct ResendOutcome(bool Success, long NewMessageId, string? Error)
{
    public static ResendOutcome Ok(long newMessageId) => new(true, newMessageId, null);
    public static ResendOutcome Failed(string error) => new(false, 0, error);
}

/// <summary>
/// Re-queues a previously-stored message for delivery to a chosen set of recipients (a fresh queue entry;
/// the original message is left untouched). Used to recover messages that some recipients never received —
/// e.g. an address wrongly skipped by the suppression list. Requires the message body to still be retained
/// (see <c>DataRetentionSettings.ResendRetentionDays</c>). Suppressed recipients are still skipped by the
/// delivery worker, so the caller should warn about / clear those first.
/// </summary>
public interface IMessageResendService
{
    /// <summary>
    /// Clones the body of <paramref name="sourceMessageId"/> into a new queued message addressed to
    /// <paramref name="recipients"/>. Tenant-scoped: only the message's own tenant can resend it.
    /// </summary>
    Task<ResendOutcome> ResendAsync(long sourceMessageId, IReadOnlyList<string> recipients, CancellationToken ct = default);
}

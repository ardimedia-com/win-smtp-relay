namespace WinSmtpRelay.Core.Models;

public class DeliveryResult
{
    public required string Recipient { get; init; }
    public required string StatusCode { get; init; }
    public required string StatusMessage { get; init; }
    public string? RemoteServer { get; init; }
    public bool Success => StatusCode.StartsWith('2');

    /// <summary>A permanent failure (5xx): the recipient is final and is not retried.</summary>
    public bool PermanentFailure => StatusCode.StartsWith('5');

    /// <summary>
    /// True only when the destination explicitly rejected THIS recipient at RCPT TO. Distinguishes a
    /// recipient-attributable failure (a genuine bad address — safe to auto-suppress) from a
    /// transaction-wide failure painted onto every recipient (all-MX-exhausted, sender/DATA rejection),
    /// which is NOT the recipient's fault and must never poison otherwise-valid co-recipients.
    /// </summary>
    public bool RecipientRejected { get; init; }

    /// <summary>
    /// True when the rejection came from a smart host (send connector or the global smart host) rather than from
    /// the recipient's own mail server. A smart host's verdict says nothing reliable about the mailbox (it may be
    /// refusing for quota, policy or its own reputation), so it never feeds the suppression list.
    /// </summary>
    public bool ViaSmartHost { get; init; }

    /// <summary>
    /// True when this recipient was never attempted because the service was shutting down. Not counted as an
    /// attempt: the message goes back to the queue without a retry penalty and without a delivery-log row.
    /// </summary>
    public bool NotAttempted { get; init; }
}

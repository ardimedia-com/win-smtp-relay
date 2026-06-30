namespace WinSmtpRelay.Core.Models;

public class DeliveryResult
{
    public required string Recipient { get; init; }
    public required string StatusCode { get; init; }
    public required string StatusMessage { get; init; }
    public string? RemoteServer { get; init; }
    public bool Success => StatusCode.StartsWith("2");

    /// <summary>
    /// True only when the destination explicitly rejected THIS recipient at RCPT TO. Distinguishes a
    /// recipient-attributable failure (a genuine bad address — safe to auto-suppress) from a
    /// transaction-wide failure painted onto every recipient (all-MX-exhausted, sender/DATA rejection),
    /// which is NOT the recipient's fault and must never poison otherwise-valid co-recipients.
    /// </summary>
    public bool RecipientRejected { get; init; }
}

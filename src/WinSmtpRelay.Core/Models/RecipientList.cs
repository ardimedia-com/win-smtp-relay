namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Helpers for the ";"-delimited recipient lists stored on a <see cref="QueuedMessage"/>
/// (<see cref="QueuedMessage.Recipients"/> and <see cref="QueuedMessage.DeliveredRecipients"/>).
/// </summary>
public static class RecipientList
{
    private static readonly char[] Separators = [';'];

    /// <summary>Splits a ";"-delimited recipient list into trimmed, non-empty addresses.</summary>
    public static string[] Split(string? list) =>
        string.IsNullOrEmpty(list)
            ? []
            : list.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The recipients in <paramref name="recipients"/> that are NOT in <paramref name="deliveredRecipients"/>
    /// (case-insensitive) — the addresses a message still needs delivered, i.e. its resend candidates.
    /// </summary>
    public static IReadOnlyList<string> Undelivered(string recipients, string? deliveredRecipients)
    {
        var delivered = new HashSet<string>(Split(deliveredRecipients), StringComparer.OrdinalIgnoreCase);
        return [.. Split(recipients).Where(r => !delivered.Contains(r))];
    }

    /// <summary>True when at least one recipient has not been delivered (the message is resendable).</summary>
    public static bool HasUndelivered(string recipients, string? deliveredRecipients) =>
        Undelivered(recipients, deliveredRecipients).Count > 0;
}

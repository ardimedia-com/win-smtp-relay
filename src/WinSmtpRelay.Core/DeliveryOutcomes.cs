using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core;

/// <summary>One delivery-log row, reduced to what the outcome count needs.</summary>
public sealed record DeliveryAttempt(long QueuedMessageId, string Recipient, string StatusCode, string? StatusMessage, DateTimeOffset TimestampUtc);

/// <summary>
/// What happened to the mail of a period, per recipient — not per attempt. A recipient whose first attempt failed
/// (smart host unreachable, timeout, TLS) and whose retry was delivered counts as delivered plus a retried attempt,
/// never as a bounce.
/// </summary>
/// <param name="Delivered">Successful deliveries (2xx).</param>
/// <param name="Bounced">Recipients that finally failed: a permanent rejection or retries exhausted (message bounced).</param>
/// <param name="Deferred">Recipients whose last attempt failed and whose message is still queued for another retry.</param>
/// <param name="Suppressed">Recipients skipped because they are on the suppression list.</param>
/// <param name="RetriedAttempts">Failed attempts of recipients that were delivered by a later attempt.</param>
public sealed record DeliveryOutcomeCounts(int Delivered, int Bounced, int Deferred, int Suppressed, int RetriedAttempts)
{
    /// <summary>Recipients with a final or pending outcome — the base of the bounce rate.</summary>
    public int Recipients => Delivered + Bounced + Deferred;

    /// <summary>Bounced share of <see cref="Recipients"/> in percent (0 when there were none).</summary>
    public double BounceRatePercent => Recipients > 0 ? 100.0 * Bounced / Recipients : 0;
}

/// <summary>Turns delivery-log rows into per-recipient outcomes. Pure — the storage layer loads the rows.</summary>
public static class DeliveryOutcomes
{
    /// <summary>
    /// Classifies <paramref name="attempts"/> (the delivery-log rows of the period). A failed attempt counts as
    /// <c>Retried</c> when the same recipient of the same message was delivered by a later attempt; otherwise the
    /// recipient counts once — as <c>Bounced</c> when its message ended bounced/failed (or no longer exists and the
    /// failure was permanent), as <c>Deferred</c> while the message is still being retried.
    /// </summary>
    /// <param name="messageStatus">The current status of the messages the failed attempts belong to (missing = purged).</param>
    public static DeliveryOutcomeCounts Classify(IEnumerable<DeliveryAttempt> attempts, IReadOnlyDictionary<long, MessageStatus> messageStatus)
    {
        var rows = attempts.ToList();
        var suppressed = rows.Count(r => IsSuppressed(r.StatusMessage));
        var delivered = rows.Count(r => r.StatusCode.StartsWith('2'));

        var failures = rows.Where(r => !IsSuppressed(r.StatusMessage) && IsFailure(r.StatusCode)).ToList();
        var lastSuccess = rows.Where(r => r.StatusCode.StartsWith('2'))
            .GroupBy(Key)
            .ToDictionary(g => g.Key, g => g.Max(r => r.TimestampUtc));

        var retried = 0;
        var bounced = 0;
        var deferred = 0;
        foreach (var group in failures.GroupBy(Key))
        {
            var lastFailure = group.MaxBy(r => r.TimestampUtc)!;
            var deliveredLater = lastSuccess.TryGetValue(group.Key, out var success) && success >= lastFailure.TimestampUtc;
            messageStatus.TryGetValue(group.Key.MessageId, out var status);
            var known = messageStatus.ContainsKey(group.Key.MessageId);

            if (deliveredLater || (known && status == MessageStatus.Delivered))
            {
                // Every failed attempt of a recipient that got its mail in the end was transient.
                retried += group.Count(r => !lastSuccess.TryGetValue(group.Key, out var s) || r.TimestampUtc <= s);
                continue;
            }

            if (known)
            {
                if (status is MessageStatus.Bounced or MessageStatus.Failed)
                    bounced++;
                else
                    deferred++; // Queued / Delivering: another attempt follows
            }
            else
            {
                // The message row is gone (purged): judge by the last attempt's own status.
                if (lastFailure.StatusCode.StartsWith('5'))
                    bounced++;
                else
                    deferred++;
            }
        }

        return new DeliveryOutcomeCounts(delivered, bounced, deferred, suppressed, retried);
    }

    private static (long MessageId, string Recipient) Key(DeliveryAttempt r) =>
        (r.QueuedMessageId, r.Recipient.Trim().ToLowerInvariant());

    private static bool IsFailure(string statusCode) => statusCode.StartsWith('4') || statusCode.StartsWith('5');

    private static bool IsSuppressed(string? message) =>
        message != null && message.StartsWith("Suppressed", StringComparison.OrdinalIgnoreCase);
}

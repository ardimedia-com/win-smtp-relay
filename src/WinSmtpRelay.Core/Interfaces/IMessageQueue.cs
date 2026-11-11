using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IMessageQueue
{
    Task<long> EnqueueAsync(QueuedMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queued messages that are due now (oldest due first), WITHOUT their bodies: <see cref="QueuedMessage.RawMessage"/>
    /// is empty on the returned objects. For listings; the delivery worker uses
    /// <see cref="GetDueCandidatesAsync"/> + <see cref="TryClaimAsync"/>.
    /// </summary>
    Task<IReadOnlyList<QueuedMessage>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(long messageId, MessageStatus status, string? error = null, CancellationToken cancellationToken = default);
    Task<QueuedMessage?> GetByIdAsync(long messageId, CancellationToken cancellationToken = default);
    Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default);
    Task SetRetryAsync(long messageId, int retryCount, DateTimeOffset nextRetryUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin operation: re-queues a Failed/Bounced message for immediate delivery (status → Queued,
    /// retry counter reset) and audits the action. Returns false when the message doesn't exist or is
    /// not in a retryable state. Distinct from the delivery worker's own status/retry plumbing
    /// (<see cref="UpdateStatusAsync"/>/<see cref="SetRetryAsync"/>), which is not an admin action.
    /// </summary>
    Task<bool> RequeueAsync(long messageId, CancellationToken cancellationToken = default);

    /// <summary>Admin operation: deletes a queued message (metadata AND body) and audits the action.</summary>
    Task DeleteAsync(long messageId, CancellationToken cancellationToken = default);

    /// <summary>The most recent messages, newest first, WITHOUT bodies (<see cref="QueuedMessage.RawMessage"/> is empty).</summary>
    Task<IReadOnlyList<QueuedMessage>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default);

    /// <summary>The most recent messages that are not delivered, newest first, WITHOUT bodies (<see cref="QueuedMessage.RawMessage"/> is empty).</summary>
    Task<IReadOnlyList<QueuedMessage>> GetNonDeliveredAsync(int maxCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent messages as body-free summaries, newest first — the form lists should use.
    /// <paramref name="nonDeliveredOnly"/> leaves out delivered messages (the queue page).
    /// </summary>
    Task<IReadOnlyList<QueuedMessageSummary>> GetSummariesAsync(int maxCount, bool nonDeliveredOnly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets messages stuck in <see cref="MessageStatus.Delivering"/> back to Queued — called once at
    /// delivery-worker startup to recover rows stranded by a crash/kill mid-delivery (the poll loop only
    /// selects Queued). Returns the number of rows requeued.
    /// </summary>
    Task<int> RequeueStaleDeliveringAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns Delivering messages whose lease expired before <paramref name="nowUtc"/> to the queue, except the ones
    /// this process is still working on (<paramref name="inFlightIds"/>). Recovers a delivery whose bookkeeping
    /// failed; the already delivered recipients are kept, so the retry does not send to them again.
    /// </summary>
    Task<int> RequeueExpiredLeasesAsync(DateTimeOffset nowUtc, IReadOnlyCollection<long> inFlightIds, CancellationToken cancellationToken = default);

    /// <summary>The tenants that have at least one message due at <paramref name="nowUtc"/>, ascending.</summary>
    Task<IReadOnlyList<int>> GetDueTenantsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Up to <paramref name="maxCount"/> due messages of one tenant, oldest due first, without bodies.</summary>
    Task<IReadOnlyList<QueueCandidate>> GetDueCandidatesAsync(int tenantId, DateTimeOffset nowUtc, int maxCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a Queued message for delivery (status → Delivering with a lease until <paramref name="leaseExpiresUtc"/>)
    /// and returns it WITH its body. Null when the message is no longer Queued (deleted, or claimed meanwhile).
    /// </summary>
    Task<QueuedMessage?> TryClaimAsync(long messageId, DateTimeOffset leaseExpiresUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome of a delivery attempt — status, delivered and failed recipients, retry state — in ONE
    /// statement, and releases the lease. Callers pass <see cref="CancellationToken.None"/> after a send: the
    /// bookkeeping of mail that left must not be skipped because the service is stopping.
    /// </summary>
    Task CompleteAttemptAsync(long messageId, DeliveryAttemptOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>Records the ";"-delimited set of recipients that have now received a 250, so a retry of
    /// this message skips them (prevents duplicate delivery to already-succeeded recipients).</summary>
    Task SetDeliveredRecipientsAsync(long messageId, string deliveredRecipients, CancellationToken cancellationToken = default);

    /// <summary>Clears a message's raw body (keeps the metadata row) — used by the strip-on-delivery
    /// data-retention policy once a message has been delivered.</summary>
    Task StripBodyAsync(long messageId, CancellationToken cancellationToken = default);
}

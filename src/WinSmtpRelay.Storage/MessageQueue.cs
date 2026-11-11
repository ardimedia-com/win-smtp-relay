using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// The admin operations (RequeueAsync/DeleteAsync) are audited at the SERVICE; the delivery worker's
// own status plumbing is not an admin action and stays unaudited. In SMTP/worker scopes the ambient
// actor is unset, so any system-initiated call is honestly recorded with a null actor.
public class MessageQueue(
    RelayDbContext db,
    ICurrentActor actor,
    IAdminAuditService audit) : IMessageQueue
{
    public async Task<long> EnqueueAsync(QueuedMessage message, CancellationToken cancellationToken = default)
    {
        db.QueuedMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return message.Id;
    }

    public async Task<IReadOnlyList<QueuedMessage>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        // An indexed range filter on (Status, NextRetryUtc): NextRetryUtc is non-nullable and stored as a sortable
        // UTC ISO string, so SQLite filters and orders it without loading any row it does not return.
        var now = DateTimeOffset.UtcNow;
        return await WithoutBody(db.QueuedMessages.AsNoTracking()
                .Where(m => m.Status == MessageStatus.Queued && m.NextRetryUtc <= now)
                .OrderBy(m => m.NextRetryUtc).ThenBy(m => m.Id)
                .Take(maxCount))
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateStatusAsync(long messageId, MessageStatus status, string? error = null, CancellationToken cancellationToken = default)
    {
        await db.QueuedMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, status)
                .SetProperty(m => m.LastError, error)
                .SetProperty(m => m.CompletedUtc, IsTerminal(status) ? DateTimeOffset.UtcNow : (DateTimeOffset?)null)
                .SetProperty(m => m.LeaseExpiresUtc, (DateTimeOffset?)null),
                cancellationToken);
    }

    private static bool IsTerminal(MessageStatus status) =>
        status is MessageStatus.Delivered or MessageStatus.Bounced or MessageStatus.Failed or MessageStatus.Suppressed;

    public async Task StripBodyAsync(long messageId, CancellationToken cancellationToken = default)
    {
        // Data minimisation: once a message is delivered its body is no longer needed. Drop the raw bytes
        // while keeping the metadata row (sender/recipients/status) for the audit/queue history.
        await db.QueuedMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RawMessage, Array.Empty<byte>()), cancellationToken);
    }

    public async Task<QueuedMessage?> GetByIdAsync(long messageId, CancellationToken cancellationToken = default)
    {
        return await db.QueuedMessages.FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
    }

    public async Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default)
    {
        return await db.QueuedMessages.CountAsync(m => m.Status == MessageStatus.Queued, cancellationToken);
    }

    public async Task SetRetryAsync(long messageId, int retryCount, DateTimeOffset nextRetryUtc, CancellationToken cancellationToken = default)
    {
        await db.QueuedMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.RetryCount, retryCount)
                .SetProperty(m => m.NextRetryUtc, nextRetryUtc),
                cancellationToken);
    }

    public async Task<bool> RequeueAsync(long messageId, CancellationToken cancellationToken = default)
    {
        // Project metadata only — no need to pull the raw body for a status change.
        var msg = await db.QueuedMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.Status, m.Sender, m.Recipients, m.TenantId })
            .FirstOrDefaultAsync(cancellationToken);
        if (msg is null || msg.Status is not (MessageStatus.Failed or MessageStatus.Bounced))
            return false;

        await db.QueuedMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MessageStatus.Queued)
                .SetProperty(m => m.LastError, (string?)null)
                .SetProperty(m => m.CompletedUtc, (DateTimeOffset?)null)
                .SetProperty(m => m.RetryCount, 0)
                .SetProperty(m => m.NextRetryUtc, DateTimeOffset.UtcNow)
                // An explicit retry by the admin: recipients that failed permanently get another chance; the
                // delivered ones stay recorded, so they do not receive the message twice.
                .SetProperty(m => m.FailedRecipients, (string?)null)
                .SetProperty(m => m.LeaseExpiresUtc, (DateTimeOffset?)null),
                cancellationToken);

        await audit.WriteAsync(AdminAuditActions.QueueMessageRequeued, actor, tenantId: msg.TenantId,
            detail: $"#{messageId} {msg.Sender} -> {msg.Recipients}", ct: cancellationToken);
        return true;
    }

    public async Task DeleteAsync(long messageId, CancellationToken cancellationToken = default)
    {
        // Project metadata only for the audit detail — the body is about to be deleted anyway.
        var msg = await db.QueuedMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.Sender, m.Recipients, m.TenantId })
            .FirstOrDefaultAsync(cancellationToken);
        if (msg is null)
            return;

        await db.QueuedMessages.Where(m => m.Id == messageId).ExecuteDeleteAsync(cancellationToken);
        await audit.WriteAsync(AdminAuditActions.QueueMessageDeleted, actor, tenantId: msg.TenantId,
            detail: $"#{messageId} {msg.Sender} -> {msg.Recipients}", ct: cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedMessage>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        return await WithoutBody(db.QueuedMessages.AsNoTracking()
                .OrderByDescending(m => m.Id)
                .Take(maxCount))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedMessage>> GetNonDeliveredAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        return await WithoutBody(db.QueuedMessages.AsNoTracking()
                .Where(m => m.Status != MessageStatus.Delivered)
                .OrderByDescending(m => m.Id)
                .Take(maxCount))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedMessageSummary>> GetSummariesAsync(int maxCount, bool nonDeliveredOnly, CancellationToken cancellationToken = default)
    {
        var query = db.QueuedMessages.AsNoTracking();
        if (nonDeliveredOnly)
            query = query.Where(m => m.Status != MessageStatus.Delivered);
        return await query
            .OrderByDescending(m => m.Id)
            .Take(maxCount)
            .Select(m => new QueuedMessageSummary(
                m.Id, m.TenantId, m.MessageId, m.Sender, m.Recipients, m.SizeBytes, m.Status, m.RetryCount,
                m.LastError, m.CreatedUtc, m.NextRetryUtc, m.CompletedUtc, m.DeliveredRecipients, m.FailedRecipients,
                m.Origin))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Projects every column except the body; the returned entities carry an empty <see cref="QueuedMessage.RawMessage"/>.</summary>
    private static IQueryable<QueuedMessage> WithoutBody(IQueryable<QueuedMessage> query) =>
        query.Select(m => new QueuedMessage
        {
            Id = m.Id,
            TenantId = m.TenantId,
            MessageId = m.MessageId,
            Sender = m.Sender,
            Recipients = m.Recipients,
            RawMessage = Array.Empty<byte>(),
            SizeBytes = m.SizeBytes,
            Status = m.Status,
            RetryCount = m.RetryCount,
            LastError = m.LastError,
            CreatedUtc = m.CreatedUtc,
            NextRetryUtc = m.NextRetryUtc,
            CompletedUtc = m.CompletedUtc,
            SourceIp = m.SourceIp,
            AuthenticatedUser = m.AuthenticatedUser,
            DeliveredRecipients = m.DeliveredRecipients,
            FailedRecipients = m.FailedRecipients,
            LeaseExpiresUtc = m.LeaseExpiresUtc,
            Origin = m.Origin,
        });

    public async Task SetDeliveredRecipientsAsync(long messageId, string deliveredRecipients, CancellationToken cancellationToken = default)
    {
        await db.QueuedMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeliveredRecipients, deliveredRecipients), cancellationToken);
    }

    public async Task<int> RequeueStaleDeliveringAsync(CancellationToken cancellationToken = default)
    {
        // Single-instance Windows service: at startup nothing is genuinely in flight, so any Delivering
        // row is a leftover from a previous crash/kill. Reset it to Queued, due now.
        var now = DateTimeOffset.UtcNow;
        return await db.QueuedMessages
            .Where(m => m.Status == MessageStatus.Delivering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MessageStatus.Queued)
                .SetProperty(m => m.NextRetryUtc, now)
                .SetProperty(m => m.LeaseExpiresUtc, (DateTimeOffset?)null),
                cancellationToken);
    }

    public async Task<int> RequeueExpiredLeasesAsync(DateTimeOffset nowUtc, IReadOnlyCollection<long> inFlightIds, CancellationToken cancellationToken = default)
    {
        var excluded = inFlightIds.ToList();
        return await db.QueuedMessages
            .Where(m => m.Status == MessageStatus.Delivering
                        && m.LeaseExpiresUtc != null && m.LeaseExpiresUtc < nowUtc
                        && !excluded.Contains(m.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MessageStatus.Queued)
                .SetProperty(m => m.NextRetryUtc, nowUtc)
                .SetProperty(m => m.LeaseExpiresUtc, (DateTimeOffset?)null),
                cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetDueTenantsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        return await db.QueuedMessages.AsNoTracking()
            .Where(m => m.Status == MessageStatus.Queued && m.NextRetryUtc <= nowUtc)
            .Select(m => m.TenantId)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueueCandidate>> GetDueCandidatesAsync(int tenantId, DateTimeOffset nowUtc, int maxCount, CancellationToken cancellationToken = default)
    {
        return await db.QueuedMessages.AsNoTracking()
            .Where(m => m.Status == MessageStatus.Queued && m.NextRetryUtc <= nowUtc && m.TenantId == tenantId)
            .OrderBy(m => m.NextRetryUtc).ThenBy(m => m.Id)
            .Take(maxCount)
            .Select(m => new QueueCandidate(m.Id, m.TenantId, m.Recipients))
            .ToListAsync(cancellationToken);
    }

    public async Task<QueuedMessage?> TryClaimAsync(long messageId, DateTimeOffset leaseExpiresUtc, CancellationToken cancellationToken = default)
    {
        // Conditional on Status == Queued, so a message deleted or claimed in between is not delivered.
        var claimed = await db.QueuedMessages
            .Where(m => m.Id == messageId && m.Status == MessageStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MessageStatus.Delivering)
                .SetProperty(m => m.LeaseExpiresUtc, leaseExpiresUtc),
                cancellationToken);
        if (claimed == 0)
            return null;

        return await db.QueuedMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
    }

    public async Task CompleteAttemptAsync(long messageId, DeliveryAttemptOutcome outcome, CancellationToken cancellationToken = default)
    {
        // One UPDATE statement = one transaction: delivered recipients and status are written together or not at all.
        DateTimeOffset? completed = IsTerminal(outcome.Status) ? DateTimeOffset.UtcNow : null;
        await db.QueuedMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, outcome.Status)
                .SetProperty(m => m.DeliveredRecipients, outcome.DeliveredRecipients)
                .SetProperty(m => m.FailedRecipients, outcome.FailedRecipients)
                .SetProperty(m => m.RetryCount, outcome.RetryCount)
                .SetProperty(m => m.NextRetryUtc, outcome.NextRetryUtc)
                .SetProperty(m => m.LastError, outcome.LastError)
                .SetProperty(m => m.CompletedUtc, completed)
                .SetProperty(m => m.LeaseExpiresUtc, (DateTimeOffset?)null),
                cancellationToken);
    }
}

using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class MessageQueue(RelayDbContext db) : IMessageQueue
{
    public async Task<long> EnqueueAsync(QueuedMessage message, CancellationToken cancellationToken = default)
    {
        db.QueuedMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return message.Id;
    }

    public async Task<IReadOnlyList<QueuedMessage>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        // SQLite/EF can't translate a range comparison on the nullable DateTimeOffset column combined
        // with an OR-null, so load the (small) Queued set ordered by Id and filter eligibility — no
        // retry scheduled, or the retry time has passed — in memory.
        var now = DateTimeOffset.UtcNow;
        var queued = await db.QueuedMessages
            .Where(m => m.Status == MessageStatus.Queued)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken);
        return queued
            .Where(m => m.NextRetryUtc is null || m.NextRetryUtc <= now)
            .Take(maxCount)
            .ToList();
    }

    public async Task UpdateStatusAsync(long messageId, MessageStatus status, string? error = null, CancellationToken cancellationToken = default)
    {
        await db.QueuedMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, status)
                .SetProperty(m => m.LastError, error)
                .SetProperty(m => m.CompletedUtc, status is MessageStatus.Delivered or MessageStatus.Bounced or MessageStatus.Failed or MessageStatus.Suppressed ? DateTimeOffset.UtcNow : (DateTimeOffset?)null),
                cancellationToken);
    }

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

    public async Task DeleteAsync(long messageId, CancellationToken cancellationToken = default)
    {
        await db.QueuedMessages.Where(m => m.Id == messageId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedMessage>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        return await db.QueuedMessages
            .OrderByDescending(m => m.Id)
            .Take(maxCount)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedMessage>> GetNonDeliveredAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        return await db.QueuedMessages
            .Where(m => m.Status != MessageStatus.Delivered)
            .OrderByDescending(m => m.Id)
            .Take(maxCount)
            .ToListAsync(cancellationToken);
    }

    public async Task SetDeliveredRecipientsAsync(long messageId, string deliveredRecipients, CancellationToken cancellationToken = default)
    {
        await db.QueuedMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeliveredRecipients, deliveredRecipients), cancellationToken);
    }

    public async Task<int> RequeueStaleDeliveringAsync(CancellationToken cancellationToken = default)
    {
        // Single-instance Windows service: at startup nothing is genuinely in flight, so any Delivering
        // row is a leftover from a previous crash/kill. Reset it to Queued (clear any retry delay).
        return await db.QueuedMessages
            .Where(m => m.Status == MessageStatus.Delivering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MessageStatus.Queued)
                .SetProperty(m => m.NextRetryUtc, (DateTimeOffset?)null),
                cancellationToken);
    }
}

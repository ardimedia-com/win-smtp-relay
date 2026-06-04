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
        return await db.QueuedMessages
            .Where(m => m.Status == MessageStatus.Queued && (m.NextRetryUtc == null || m.NextRetryUtc <= DateTime.UtcNow))
            .OrderBy(m => m.NextRetryUtc ?? m.CreatedUtc)
            .Take(maxCount)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateStatusAsync(long messageId, MessageStatus status, string? error = null, CancellationToken cancellationToken = default)
    {
        await db.QueuedMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, status)
                .SetProperty(m => m.LastError, error)
                .SetProperty(m => m.CompletedUtc, status is MessageStatus.Delivered or MessageStatus.Bounced ? DateTime.UtcNow : (DateTime?)null),
                cancellationToken);
    }

    public async Task<QueuedMessage?> GetByIdAsync(long messageId, CancellationToken cancellationToken = default)
    {
        return await db.QueuedMessages.FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
    }

    public async Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default)
    {
        return await db.QueuedMessages.CountAsync(m => m.Status == MessageStatus.Queued, cancellationToken);
    }

    public async Task SetRetryAsync(long messageId, int retryCount, DateTime nextRetryUtc, CancellationToken cancellationToken = default)
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
            .OrderByDescending(m => m.CreatedUtc)
            .Take(maxCount)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedMessage>> GetNonDeliveredAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        return await db.QueuedMessages
            .Where(m => m.Status != MessageStatus.Delivered)
            .OrderByDescending(m => m.CreatedUtc)
            .Take(maxCount)
            .ToListAsync(cancellationToken);
    }
}

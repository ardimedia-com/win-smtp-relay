using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IMessageQueue
{
    Task<long> EnqueueAsync(QueuedMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QueuedMessage>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(long messageId, MessageStatus status, string? error = null, CancellationToken cancellationToken = default);
    Task<QueuedMessage?> GetByIdAsync(long messageId, CancellationToken cancellationToken = default);
    Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default);
    Task SetRetryAsync(long messageId, int retryCount, DateTimeOffset nextRetryUtc, CancellationToken cancellationToken = default);
    Task DeleteAsync(long messageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QueuedMessage>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QueuedMessage>> GetNonDeliveredAsync(int maxCount, CancellationToken cancellationToken = default);
}

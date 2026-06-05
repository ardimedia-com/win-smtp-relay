using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Delivery;

public class DeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IActivityNotifier activityNotifier,
    IOptions<DeliveryOptions> options,
    IRuntimeConfigCache configCache,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        logger.LogInformation("Delivery worker starting with {MaxConcurrent} concurrent deliveries",
            config.MaxConcurrentDeliveries);

        using var semaphore = new SemaphoreSlim(config.MaxConcurrentDeliveries);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for a free delivery slot before fetching work
                await semaphore.WaitAsync(stoppingToken);

                IReadOnlyList<QueuedMessage> pending;
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();
                    pending = await queue.GetPendingAsync(1, stoppingToken);

                    if (pending.Count == 0)
                    {
                        semaphore.Release();
                        await Task.Delay(PollInterval, stoppingToken);
                        continue;
                    }

                    // Mark as Delivering BEFORE Task.Run to prevent the next loop
                    // iteration from picking up the same message again
                    await queue.UpdateStatusAsync(pending[0].Id, MessageStatus.Delivering, cancellationToken: stoppingToken);
                    _ = activityNotifier.NotifyQueueChangedAsync();
                }
                catch
                {
                    semaphore.Release();
                    throw;
                }

                // Fire and forget — semaphore is released when processing completes
                var message = pending[0];
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessMessageAsync(message, stoppingToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Delivery worker error");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        logger.LogInformation("Delivery worker shutting down");
    }

    private async Task ProcessMessageAsync(QueuedMessage message, CancellationToken cancellationToken)
    {
        var config = options.Value;

        using var scope = scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();
        var deliveryService = scope.ServiceProvider.GetRequiredService<IDeliveryService>();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();

        try
        {
            // Run message filters before delivery
            var filters = scope.ServiceProvider.GetServices<IMessageFilter>().OrderBy(f => f.Order);
            var filterContext = new MessageFilterContext
            {
                RawMessage = message.RawMessage,
                Sender = message.Sender,
                Recipients = message.Recipients,
                SourceIp = message.SourceIp,
                TenantId = message.TenantId
            };

            foreach (var filter in filters)
            {
                var result = await filter.FilterAsync(filterContext, cancellationToken);
                if (!result.Accept)
                {
                    await queue.UpdateStatusAsync(message.Id, MessageStatus.Bounced, $"Filtered: {result.RejectReason}", cancellationToken);
                    _ = activityNotifier.NotifyQueueChangedAsync();
                    logger.LogInformation("Message {MessageId} rejected by filter: {Reason}",
                        message.MessageId, result.RejectReason);

                    // Log filter rejection for each recipient
                    var recipients = message.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var recipient in recipients)
                    {
                        await LogDeliveryAsync(db, message.Id, recipient, "550", $"Filtered: {result.RejectReason}", null, message.TenantId);
                        _ = activityNotifier.NotifyDeliveryAttemptAsync(message.MessageId, recipient, "550", null, message.TenantId);
                    }

                    return;
                }
                if (result.ModifiedRawMessage != null)
                {
                    filterContext.RawMessage = result.ModifiedRawMessage;
                    message.RawMessage = result.ModifiedRawMessage;
                    message.Sender = filterContext.Sender;
                }
            }

            var deliveryResults = await deliveryService.DeliverAsync(message, cancellationToken);
            await queue.UpdateStatusAsync(message.Id, MessageStatus.Delivered, cancellationToken: cancellationToken);
            _ = activityNotifier.NotifyQueueChangedAsync();

            // Log per-recipient delivery results and broadcast via SignalR
            foreach (var dr in deliveryResults)
            {
                await LogDeliveryAsync(db, message.Id, dr.Recipient, dr.StatusCode, dr.StatusMessage, dr.RemoteServer, message.TenantId);
                _ = activityNotifier.NotifyDeliveryAttemptAsync(message.MessageId, dr.Recipient, dr.StatusCode, dr.RemoteServer, message.TenantId);
            }

            logger.LogInformation("Message {MessageId} (id={QueueId}) delivered successfully",
                message.MessageId, message.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delivery failed for message {MessageId} (id={QueueId}), attempt {Attempt}",
                message.MessageId, message.Id, message.RetryCount + 1);

            // Log per-recipient results if available (from DeliveryException)
            if (ex is DeliveryException dex)
            {
                foreach (var dr in dex.Results)
                {
                    await LogDeliveryAsync(db, message.Id, dr.Recipient, dr.StatusCode, dr.StatusMessage, dr.RemoteServer, message.TenantId);
                    _ = activityNotifier.NotifyDeliveryAttemptAsync(message.MessageId, dr.Recipient, dr.StatusCode, dr.RemoteServer, message.TenantId);
                }
            }
            else
            {
                // Generic failure — log for all recipients
                var recipients = message.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var recipient in recipients)
                {
                    await LogDeliveryAsync(db, message.Id, recipient, "500", ex.Message, null, message.TenantId);
                    _ = activityNotifier.NotifyDeliveryAttemptAsync(message.MessageId, recipient, "500", null, message.TenantId);
                }
            }

            message.RetryCount++;
            message.LastError = ex.Message;

            // Use extended hold time for backup MX domains (read live from the runtime config cache)
            var effectiveConfig = config;
            var backupMx = await configCache.GetBackupMxSettingsAsync(cancellationToken);
            if (backupMx.Enabled && IsBackupMxMessage(message, backupMx))
            {
                effectiveConfig = new DeliveryOptions
                {
                    MaxRetryHours = backupMx.MaxHoldHours,
                    RetryIntervalsMinutes = [backupMx.RetryIntervalMinutes]
                };
            }

            var nextRetry = CalculateNextRetry(message.RetryCount, effectiveConfig);

            if (nextRetry == null || IsPermanentFailure(ex))
            {
                await queue.UpdateStatusAsync(message.Id, MessageStatus.Bounced, ex.Message, cancellationToken);
                logger.LogWarning("Message {MessageId} (id={QueueId}) bounced: {Error}",
                    message.MessageId, message.Id, ex.Message);
            }
            else
            {
                await queue.UpdateStatusAsync(message.Id, MessageStatus.Queued, ex.Message, cancellationToken);
                await queue.SetRetryAsync(message.Id, message.RetryCount, nextRetry.Value, cancellationToken);
            }
            _ = activityNotifier.NotifyQueueChangedAsync();
        }
    }

    private static async Task LogDeliveryAsync(
        RelayDbContext db, long queuedMessageId, string recipient,
        string statusCode, string statusMessage, string? remoteServer, int tenantId)
    {
        db.DeliveryLogs.Add(new DeliveryLog
        {
            QueuedMessageId = queuedMessageId,
            TenantId = tenantId,
            Recipient = recipient,
            StatusCode = statusCode,
            StatusMessage = statusMessage,
            RemoteServer = remoteServer,
            TimestampUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    internal static DateTimeOffset? CalculateNextRetry(int retryCount, DeliveryOptions config)
    {
        if (retryCount <= 0)
            return DateTimeOffset.UtcNow;

        var intervals = config.RetryIntervalsMinutes;
        if (intervals.Length == 0)
            return null;

        var intervalIndex = Math.Min(retryCount - 1, intervals.Length - 1);
        var delayMinutes = intervals[intervalIndex];

        // Check if total retry time exceeds max window
        // Sum configured intervals, then add repeated last interval for retries beyond the array
        var totalMinutes = intervals.Sum();
        if (retryCount > intervals.Length)
            totalMinutes += (retryCount - intervals.Length) * intervals[^1];
        if (totalMinutes > config.MaxRetryHours * 60)
            return null;

        return DateTimeOffset.UtcNow.AddMinutes(delayMinutes);
    }

    private static bool IsBackupMxMessage(QueuedMessage message, BackupMxSettings backupMx)
    {
        var recipientDomains = message.Recipients
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Split('@').Last());

        return recipientDomains.Any(domain =>
            backupMx.DomainList.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsPermanentFailure(Exception ex)
    {
        var message = ex.Message;
        return message.StartsWith("5") ||
               message.Contains("550") ||
               message.Contains("551") ||
               message.Contains("552") ||
               message.Contains("553") ||
               message.Contains("554");
    }
}

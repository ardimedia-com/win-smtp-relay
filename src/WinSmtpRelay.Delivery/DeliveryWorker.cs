using MailKit.Net.Smtp;
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

        // Recover any messages stranded in Delivering by a previous crash/kill (the poll loop only
        // selects Queued, so otherwise they would never be retried).
        try
        {
            using var startupScope = scopeFactory.CreateScope();
            var startupQueue = startupScope.ServiceProvider.GetRequiredService<IMessageQueue>();
            var requeued = await startupQueue.RequeueStaleDeliveringAsync(stoppingToken);
            if (requeued > 0)
                logger.LogWarning("Re-queued {Count} message(s) left in Delivering by a previous shutdown", requeued);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not requeue stale Delivering messages at startup");
        }

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

    internal async Task ProcessMessageAsync(QueuedMessage message, CancellationToken cancellationToken)
    {
        var config = options.Value;

        using var scope = scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();
        var deliveryService = scope.ServiceProvider.GetRequiredService<IDeliveryService>();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();

        var suppression = scope.ServiceProvider.GetRequiredService<ISuppressionService>();

        try
        {
            // Suppression: never deliver to addresses on the tenant's suppression list (prior hard bounce
            // or complaint). Skipped recipients are logged; if every recipient is suppressed the message
            // is terminal (Suppressed) and not retried. Re-evaluated each attempt against the original
            // recipient list, so it stays correct across retries.
            var allRecipients = message.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var deliverable = new List<string>();
            foreach (var r in allRecipients)
            {
                if (await suppression.IsSuppressedAsync(r, message.TenantId, cancellationToken))
                {
                    // Record the skip once (first attempt only) so the reporting digest doesn't re-count
                    // the same suppressed recipient on every retry of a mixed message.
                    if (message.RetryCount == 0)
                    {
                        await LogDeliveryAsync(db, message.Id, r, "550", "Suppressed: address previously hard-bounced or complained", null, message.TenantId);
                        _ = activityNotifier.NotifyDeliveryAttemptAsync(message.MessageId, r, "550", null, message.TenantId);
                    }
                    logger.LogInformation("Message {MessageId}: recipient {Recipient} skipped (suppressed)", message.MessageId, r);
                }
                else
                {
                    deliverable.Add(r);
                }
            }
            if (deliverable.Count == 0)
            {
                await queue.UpdateStatusAsync(message.Id, MessageStatus.Suppressed, "All recipients suppressed", cancellationToken);
                _ = activityNotifier.NotifyQueueChangedAsync();
                logger.LogInformation("Message {MessageId} (id={QueueId}) not sent: all recipients suppressed", message.MessageId, message.Id);
                return;
            }
            // Deliver only to the non-suppressed recipients (local mutation; the persisted row keeps the
            // original recipients, so a retry re-evaluates suppression from scratch).
            message.Recipients = string.Join(';', deliverable);

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

            // Record the recipients that actually received the message (a full success delivered every
            // deliverable recipient), so the undelivered ones — those skipped by the suppression list — stay
            // identifiable for a later resend. The persisted Recipients column keeps the ORIGINAL list.
            await queue.SetDeliveredRecipientsAsync(message.Id, string.Join(';', deliverable), cancellationToken);

            // Data-retention policy: drop the message body once delivered (the metadata row stays for the
            // queue/audit history). Two cases keep the body: archive/regulated profiles (StripBodyOnDelivery
            // off), and the resend grace — a message with undelivered (suppressed) recipients keeps its body for
            // ResendRetentionDays so an admin can resend it; the nightly purge drops it after that window.
            // Best-effort: this runs after delivery already succeeded, so a settings-read hiccup must never
            // revert the message to Queued — the nightly retention purge removes the body either way.
            try
            {
                var retention = scope.ServiceProvider.GetService<IDataRetentionSettingsService>();
                if (retention is not null)
                {
                    var settings = await retention.GetAsync(cancellationToken);
                    var hasUndelivered = allRecipients.Any(r => !deliverable.Contains(r, StringComparer.OrdinalIgnoreCase));
                    var keepForResend = settings.ResendRetentionDays > 0 && hasUndelivered;
                    if (settings.StripBodyOnDelivery && !keepForResend)
                        await queue.StripBodyAsync(message.Id, cancellationToken);
                }
            }
            catch (Exception stripEx)
            {
                logger.LogWarning(stripEx, "Could not strip body for delivered message {QueueId}", message.Id);
            }

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
            // Service shutdown cancelled an in-flight delivery — not a real failure. Re-queue it (with a
            // non-cancelled token so the write isn't skipped) without logging a fake attempt or bumping
            // RetryCount, so it is retried cleanly on next start rather than stranded in Delivering.
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                try { await queue.UpdateStatusAsync(message.Id, MessageStatus.Queued, cancellationToken: CancellationToken.None); }
                catch (Exception requeueEx) { logger.LogWarning(requeueEx, "Failed to re-queue message {QueueId} during shutdown", message.Id); }
                return;
            }

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

                // Record recipients that succeeded this attempt so a retry skips them (no duplicates
                // to recipients/domains that already accepted the message).
                var delivered = new HashSet<string>(
                    (message.DeliveredRecipients ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
                var before = delivered.Count;
                foreach (var dr in dex.Results.Where(r => r.Success))
                    delivered.Add(dr.Recipient);
                if (delivered.Count != before)
                    await queue.SetDeliveredRecipientsAsync(message.Id, string.Join(';', delivered), cancellationToken);

                // Auto-suppress recipients the destination explicitly rejected at RCPT TO with a permanent
                // (5xx) code. Repeatedly mailing dead addresses is a primary cause of blocklisting; future
                // messages to them are skipped. The RecipientRejected guard is essential: a transaction-wide
                // 5xx (sender/DATA rejection, all-MX-exhausted) is painted onto every recipient but is NOT
                // the recipient's fault — suppressing on it would poison valid co-recipients that merely
                // shared an envelope with one bad address.
                foreach (var dr in dex.Results.Where(r => r.RecipientRejected && !r.Success && r.StatusCode.StartsWith('5')))
                    await suppression.AddAsync(dr.Recipient, SuppressionReason.HardBounce,
                        $"{dr.StatusCode} {dr.StatusMessage}", message.TenantId, cancellationToken);
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
        // Classify on structured status, not free-text substring matching. A DeliveryException carries
        // per-recipient results — it is permanent only if EVERY failing recipient got a 5xx (a single
        // 4xx/transient among them must retry). Raw exceptions (smart-host/route path) are permanent
        // only for an actual 5xx SMTP command rejection; connection/timeout/protocol failures retry.
        if (ex is DeliveryException dex)
        {
            var failures = dex.Results.Where(r => !r.Success).ToList();
            return failures.Count > 0 && failures.All(r => r.StatusCode.StartsWith('5'));
        }
        return ex is SmtpCommandException sce && (int)sce.StatusCode >= 500;
    }
}

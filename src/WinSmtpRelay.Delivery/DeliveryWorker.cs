using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Delivery;

/// <summary>
/// Picks due messages fairly (tenants in turn, a cap per destination domain), claims each with a lease, delivers
/// it, and records the outcome per recipient. The outcome — delivered recipients, permanently failed recipients,
/// status and retry time — is written in one statement right after the send, with retries and without the
/// shutdown token, so mail that left is never sent again because the bookkeeping was lost (H1/H2). A periodic
/// sweep returns messages whose lease expired (a delivery that died between send and bookkeeping) to the queue.
/// </summary>
public class DeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IActivityNotifier activityNotifier,
    IOptions<DeliveryOptions> options,
    IRuntimeConfigCache configCache,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Re-check sooner when due mail exists but every candidate waits for a busy destination domain.</summary>
    private static readonly TimeSpan BusyPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a claim on a message holds. In-flight messages of this process are never swept, so this
    /// only bounds the recovery of a delivery whose bookkeeping failed.</summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan LeaseSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long a stopping service waits for in-flight deliveries before it cancels them.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(20);

    /// <summary>Due candidates looked at per tenant and pick.</summary>
    private const int CandidatesPerTenant = 50;

    private static readonly TimeSpan[] PersistRetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    private readonly DeliveryScheduler _scheduler = new(options.Value.MaxConcurrentDeliveriesPerDomain);
    private readonly ConcurrentDictionary<long, Task> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        logger.LogInformation("Delivery worker starting with {MaxConcurrent} concurrent deliveries, at most {PerDomain} per destination domain",
            config.MaxConcurrentDeliveries, _scheduler.PerDomainCap);

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
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not requeue stale Delivering messages at startup");
        }

        // Deliveries run on their own token: a stopping service lets them finish (up to ShutdownGrace) instead of
        // cutting an SMTP transaction in the middle of DATA.
        using var deliveryCts = new CancellationTokenSource();
        // Not disposed: a delivery that outlives the grace period still releases its slot.
        var slots = new SemaphoreSlim(Math.Max(1, config.MaxConcurrentDeliveries));
        var lastSweep = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var started = false;
            var dueButBusy = false;
            try
            {
                await slots.WaitAsync(stoppingToken);
                try
                {
                    if (DateTimeOffset.UtcNow - lastSweep >= LeaseSweepInterval)
                    {
                        lastSweep = DateTimeOffset.UtcNow;
                        await SweepExpiredLeasesAsync(stoppingToken);
                    }

                    (started, dueButBusy) = await TryStartNextAsync(slots, deliveryCts.Token, stoppingToken);
                }
                finally
                {
                    if (!started)
                        slots.Release();
                }

                if (!started)
                    await Task.Delay(dueButBusy ? BusyPollInterval : PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Delivery worker error");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        await DrainAsync(deliveryCts);
        logger.LogInformation("Delivery worker shut down");
    }

    /// <summary>Picks the next message (tenants in turn, domain cap), claims it, and starts its delivery.</summary>
    private async Task<(bool Started, bool DueButBusy)> TryStartNextAsync(SemaphoreSlim slots, CancellationToken deliveryToken, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();
        var now = DateTimeOffset.UtcNow;

        var tenants = await queue.GetDueTenantsAsync(now, ct);
        foreach (var tenantId in _scheduler.TenantOrder(tenants))
        {
            var candidates = await queue.GetDueCandidatesAsync(tenantId, now, CandidatesPerTenant, ct);
            foreach (var candidate in candidates)
            {
                if (!_scheduler.CanStart(candidate))
                    continue;

                var message = await queue.TryClaimAsync(candidate.Id, DateTimeOffset.UtcNow + LeaseDuration, ct);
                if (message is null)
                    continue; // deleted or claimed meanwhile

                _scheduler.Started(message.Id, message.TenantId, message.Recipients);
                _ = activityNotifier.NotifyQueueChangedAsync();
                _running[message.Id] = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessMessageAsync(message, deliveryToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Unhandled error delivering message {QueueId}; the lease sweep will return it to the queue", message.Id);
                    }
                    finally
                    {
                        _scheduler.Finished(message.Id);
                        _running.TryRemove(message.Id, out _);
                        slots.Release();
                    }
                }, CancellationToken.None);
                return (true, false);
            }
        }

        return (false, tenants.Count > 0);
    }

    private async Task SweepExpiredLeasesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();
            var requeued = await queue.RequeueExpiredLeasesAsync(DateTimeOffset.UtcNow, _scheduler.InFlightIds, ct);
            if (requeued > 0)
            {
                logger.LogWarning("Returned {Count} message(s) with an expired delivery lease to the queue", requeued);
                _ = activityNotifier.NotifyQueueChangedAsync();
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Delivery lease sweep failed");
        }
    }

    /// <summary>Waits for in-flight deliveries on shutdown, then cancels the ones still running.</summary>
    private async Task DrainAsync(CancellationTokenSource deliveryCts)
    {
        var running = _running.Values.ToArray();
        if (running.Length == 0)
            return;

        logger.LogInformation("Waiting up to {Seconds}s for {Count} in-flight deliveries", ShutdownGrace.TotalSeconds, running.Length);
        var all = Task.WhenAll(running);
        if (await Task.WhenAny(all, Task.Delay(ShutdownGrace)) == all)
            return;

        logger.LogWarning("In-flight deliveries did not finish within {Seconds}s — cancelling them; they are re-queued", ShutdownGrace.TotalSeconds);
        await deliveryCts.CancelAsync();
        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    internal async Task ProcessMessageAsync(QueuedMessage message, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var all = RecipientList.Split(message.Recipients);
        var delivered = new HashSet<string>(RecipientList.Split(message.DeliveredRecipients), StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(RecipientList.Split(message.FailedRecipients), StringComparer.OrdinalIgnoreCase);
        var pending = all.Where(r => !delivered.Contains(r) && !failed.Contains(r)).ToList();

        // System mail (reports, alerts) is exempt from the suppression list: a bounce of the report recipient must
        // never silence the reports themselves.
        var exempt = message.Origin == MessageOrigin.System;

        var deliverable = new List<string>();
        var suppressedNow = new List<string>();
        var results = new List<DeliveryResult>();
        string? filterRejection = null;

        try
        {
            // Suppression: never deliver to addresses on the tenant's suppression list (prior hard bounce
            // or complaint). Re-evaluated each attempt for the recipients still pending.
            var suppression = sp.GetRequiredService<ISuppressionService>();
            foreach (var r in pending)
            {
                if (!exempt && await suppression.IsSuppressedAsync(r, message.TenantId, cancellationToken))
                {
                    suppressedNow.Add(r);
                    logger.LogInformation("Message {MessageId}: recipient {Recipient} skipped (suppressed)", message.MessageId, r);
                }
                else
                {
                    deliverable.Add(r);
                }
            }

            if (deliverable.Count > 0)
            {
                // Run message filters before delivery
                var filters = sp.GetServices<IMessageFilter>().OrderBy(f => f.Order);
                var filterContext = new MessageFilterContext
                {
                    RawMessage = message.RawMessage,
                    Sender = message.Sender,
                    Recipients = string.Join(';', deliverable),
                    SourceIp = message.SourceIp,
                    TenantId = message.TenantId
                };

                foreach (var filter in filters)
                {
                    var result = await filter.FilterAsync(filterContext, cancellationToken);
                    if (!result.Accept)
                    {
                        filterRejection = $"Filtered: {result.RejectReason}";
                        logger.LogInformation("Message {MessageId} rejected by filter: {Reason}", message.MessageId, result.RejectReason);
                        break;
                    }
                    if (result.ModifiedRawMessage != null)
                    {
                        filterContext.RawMessage = result.ModifiedRawMessage;
                        message.RawMessage = result.ModifiedRawMessage;
                        message.Sender = filterContext.Sender;
                    }
                }

                if (filterRejection is not null)
                {
                    results.AddRange(deliverable.Select(r => new DeliveryResult { Recipient = r, StatusCode = "550", StatusMessage = filterRejection }));
                }
                else
                {
                    // Deliver only to the recipients still pending and not suppressed (a copy — the persisted row
                    // keeps the original recipient list).
                    var attempt = CopyFor(message, deliverable);
                    var deliveryService = sp.GetRequiredService<IDeliveryService>();
                    try
                    {
                        results.AddRange(await deliveryService.DeliverAsync(attempt, cancellationToken));
                    }
                    catch (DeliveryException dex)
                    {
                        results.AddRange(dex.Results);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service shutdown before or during the send: nothing more is attempted; whatever results exist are
            // kept, the rest goes back to the queue without a retry penalty.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delivery failed for message {MessageId} (id={QueueId}), attempt {Attempt}",
                message.MessageId, message.Id, message.RetryCount + 1);
            var settled = results.Select(r => r.Recipient).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unsettled = pending.Where(r => !suppressedNow.Contains(r) && !settled.Contains(r)).ToList();
            results.AddRange(SmtpDeliveryService.FailureResults(unsettled, ex, remoteServer: null, viaSmartHost: false));
        }

        // Every pending recipient that is not suppressed is decided by its result; one without a result (an
        // interruption before it was reached) stays pending.
        var considered = pending.Where(r => !suppressedNow.Contains(r)).ToList();
        var outcome = await DecideAsync(message, all, delivered, failed, considered, suppressedNow, results, cancellationToken.IsCancellationRequested);

        // 1. The state first, in one statement, with retries and without the shutdown token: this is what keeps a
        // retry from sending to the recipients that already have the message.
        if (!await PersistAsync(message.Id, outcome.Attempt))
            return; // logged; the lease sweep returns the message to the queue later

        _ = activityNotifier.NotifyQueueChangedAsync();
        if (outcome.Attempt.Status == MessageStatus.Delivered)
            logger.LogInformation("Message {MessageId} (id={QueueId}) delivered", message.MessageId, message.Id);
        else if (outcome.Attempt.Status is MessageStatus.Bounced or MessageStatus.Suppressed)
            logger.LogWarning("Message {MessageId} (id={QueueId}) {Status}: {Error}", message.MessageId, message.Id, outcome.Attempt.Status, outcome.Attempt.LastError);

        // 2. Everything else is best-effort: a failure here must never undo or repeat the delivery.
        await BestEffortAsync("write the delivery log", () => LogResultsAsync(sp, message, results, suppressedNow));
        if (!exempt)
            await BestEffortAsync("update the suppression list", () => SuppressAsync(sp, message, results));

        if (outcome.Terminal)
        {
            if (outcome.Attempt.Status == MessageStatus.Delivered)
                await BestEffortAsync("strip the delivered body", () => StripBodyAsync(sp, message, hasUndelivered: outcome.Undelivered.Count > 0));

            if (outcome.Undelivered.Count > 0 && message.Origin == MessageOrigin.Submitted)
                await BestEffortAsync("queue a delivery status notification",
                    () => sp.GetRequiredService<IDsnService>().NotifyAsync(message, outcome.Undelivered, CancellationToken.None));
        }
    }

    /// <summary>The decided state of a message after one attempt.</summary>
    private sealed record Outcome(DeliveryAttemptOutcome Attempt, bool Terminal, IReadOnlyList<DsnRecipient> Undelivered);

    private async Task<Outcome> DecideAsync(
        QueuedMessage message, IReadOnlyList<string> all, HashSet<string> delivered, HashSet<string> failed,
        IReadOnlyList<string> considered, IReadOnlyList<string> suppressedNow, IReadOnlyList<DeliveryResult> results,
        bool shuttingDown)
    {
        var byRecipient = new Dictionary<string, DeliveryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
            byRecipient[r.Recipient] = r;

        var stillPending = new List<string>();
        var attempted = false;
        foreach (var r in considered)
        {
            if (!byRecipient.TryGetValue(r, out var result) || result.NotAttempted)
            {
                stillPending.Add(r);
                continue;
            }
            attempted = true;
            if (result.Success)
                delivered.Add(r);
            else if (result.PermanentFailure)
                failed.Add(r);
            else
                stillPending.Add(r);
        }

        var failures = results.Where(r => !r.Success && !r.NotAttempted).ToList();
        var lastError = failures.Count == 0
            ? null
            : $"Delivery failed for {failures.Count} recipient(s): {string.Join("; ", failures.Select(f => $"{f.Recipient}: {f.StatusCode} {f.StatusMessage}"))}";
        var retryCount = message.RetryCount + (attempted ? 1 : 0);
        var now = DateTimeOffset.UtcNow;

        MessageStatus status;
        var terminal = true;
        var nextRetry = now;
        if (stillPending.Count == 0)
        {
            status = failed.Count > 0 ? MessageStatus.Bounced
                : delivered.Count > 0 ? MessageStatus.Delivered
                : MessageStatus.Suppressed;
            if (status == MessageStatus.Suppressed)
                lastError = "All recipients suppressed";
        }
        else if (shuttingDown && !attempted)
        {
            // Stopped before anything was tried: back to the queue, due now, no retry penalty.
            status = MessageStatus.Queued;
            terminal = false;
            retryCount = message.RetryCount;
            lastError = message.LastError;
        }
        else
        {
            // Use extended hold time for backup MX domains (read live from the runtime config cache)
            var effectiveConfig = options.Value;
            var backupMx = await configCache.GetBackupMxSettingsAsync(CancellationToken.None);
            if (backupMx.Enabled && IsBackupMxMessage(stillPending, backupMx))
            {
                effectiveConfig = new DeliveryOptions
                {
                    MaxRetryHours = backupMx.MaxHoldHours,
                    RetryIntervalsMinutes = [backupMx.RetryIntervalMinutes]
                };
            }

            var next = shuttingDown ? now : CalculateNextRetry(retryCount, effectiveConfig);
            if (next is null)
            {
                // Retry window exhausted: the temporary failures become final.
                foreach (var r in stillPending)
                    failed.Add(r);
                status = MessageStatus.Bounced;
                lastError = $"Retry window exhausted. {lastError}".Trim();
            }
            else
            {
                status = MessageStatus.Queued;
                terminal = false;
                nextRetry = next.Value;
            }
        }

        var undelivered = terminal
            ? all.Where(r => !delivered.Contains(r)).Select(r => ToDsnRecipient(r, byRecipient, suppressedNow)).ToList()
            : [];

        return new Outcome(
            new DeliveryAttemptOutcome(status, Join(delivered, all), Join(failed, all), retryCount, nextRetry, lastError),
            terminal,
            undelivered);
    }

    private static DsnRecipient ToDsnRecipient(string recipient, Dictionary<string, DeliveryResult> byRecipient, IReadOnlyList<string> suppressedNow)
    {
        if (suppressedNow.Contains(recipient, StringComparer.OrdinalIgnoreCase))
            return new DsnRecipient(recipient, "550", null, null, Suppressed: true);
        return byRecipient.TryGetValue(recipient, out var r) && !r.NotAttempted
            ? new DsnRecipient(recipient, r.StatusCode, r.StatusMessage, r.RemoteServer)
            : new DsnRecipient(recipient, null, "Delivery failed on an earlier attempt (see the relay's journal)", null);
    }

    /// <summary>The recipients of <paramref name="set"/> in the order of the original list, ";"-joined (null when empty).</summary>
    private static string? Join(HashSet<string> set, IReadOnlyList<string> order)
    {
        var ordered = order.Where(set.Contains).Concat(set.Where(s => !order.Contains(s, StringComparer.OrdinalIgnoreCase))).ToList();
        return ordered.Count == 0 ? null : string.Join(';', ordered);
    }

    private async Task<bool> PersistAsync(long messageId, DeliveryAttemptOutcome outcome)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // A fresh scope per try: a context that failed once is not reused.
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMessageQueue>()
                    .CompleteAttemptAsync(messageId, outcome, CancellationToken.None);
                return true;
            }
            catch (Exception ex) when (attempt < PersistRetryDelays.Length)
            {
                logger.LogWarning(ex, "Recording the delivery outcome of message {QueueId} failed — retrying", messageId);
                await Task.Delay(PersistRetryDelays[attempt]);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Recording the delivery outcome of message {QueueId} failed after {Attempts} attempts; it stays Delivering until its lease expires",
                    messageId, attempt + 1);
                return false;
            }
        }
    }

    private async Task BestEffortAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not {What} after a delivery attempt", what);
        }
    }

    private async Task LogResultsAsync(IServiceProvider sp, QueuedMessage message, IReadOnlyList<DeliveryResult> results, IReadOnlyList<string> suppressedNow)
    {
        var db = sp.GetRequiredService<RelayDbContext>();
        var now = DateTimeOffset.UtcNow;

        // Record a suppression skip once (first attempt only) so the reporting digest doesn't re-count the same
        // suppressed recipient on every retry of a mixed message.
        if (message.RetryCount == 0)
        {
            foreach (var r in suppressedNow)
                db.DeliveryLogs.Add(NewLog(message, r, "550", "Suppressed: address previously hard-bounced or complained", null, now));
        }
        foreach (var r in results.Where(r => !r.NotAttempted))
            db.DeliveryLogs.Add(NewLog(message, r.Recipient, r.StatusCode, r.StatusMessage, r.RemoteServer, now));
        await db.SaveChangesAsync(CancellationToken.None);

        if (message.RetryCount == 0)
            foreach (var r in suppressedNow)
                _ = activityNotifier.NotifyDeliveryAttemptAsync(message.MessageId, r, "550", null, message.TenantId);
        foreach (var r in results.Where(r => !r.NotAttempted))
            _ = activityNotifier.NotifyDeliveryAttemptAsync(message.MessageId, r.Recipient, r.StatusCode, r.RemoteServer, message.TenantId);
    }

    private static DeliveryLog NewLog(QueuedMessage message, string recipient, string code, string text, string? remote, DateTimeOffset now) => new()
    {
        QueuedMessageId = message.Id,
        TenantId = message.TenantId,
        Sender = message.Sender,
        Recipient = recipient,
        StatusCode = code,
        StatusMessage = text,
        RemoteServer = remote,
        TimestampUtc = now
    };

    /// <summary>
    /// Auto-suppresses recipients whose rejection says the address does not exist (T1075, see
    /// <see cref="SuppressionPolicy"/>): never a policy block, never a smart host's verdict, never a
    /// transaction-wide failure painted onto co-recipients.
    /// </summary>
    private static async Task SuppressAsync(IServiceProvider sp, QueuedMessage message, IReadOnlyList<DeliveryResult> results)
    {
        var suppression = sp.GetRequiredService<ISuppressionService>();
        foreach (var r in results.Where(SuppressionPolicy.ShouldSuppress))
            await suppression.AddAsync(r.Recipient, SuppressionReason.HardBounce, $"{r.StatusCode} {r.StatusMessage}", message.TenantId, CancellationToken.None);
    }

    /// <summary>
    /// Data-retention policy: drop the message body once delivered (the metadata row stays for the queue/audit
    /// history). Two cases keep the body: archive/regulated profiles (StripBodyOnDelivery off), and the resend
    /// grace — a message with undelivered recipients keeps its body for ResendRetentionDays so an admin can resend
    /// it; the nightly purge drops it after that window.
    /// </summary>
    private static async Task StripBodyAsync(IServiceProvider sp, QueuedMessage message, bool hasUndelivered)
    {
        var retention = sp.GetService<IDataRetentionSettingsService>();
        if (retention is null)
            return;
        var settings = await retention.GetAsync(CancellationToken.None);
        var keepForResend = settings.ResendRetentionDays > 0 && hasUndelivered;
        if (settings.StripBodyOnDelivery && !keepForResend)
            await sp.GetRequiredService<IMessageQueue>().StripBodyAsync(message.Id, CancellationToken.None);
    }

    private static QueuedMessage CopyFor(QueuedMessage message, IReadOnlyList<string> recipients) => new()
    {
        Id = message.Id,
        TenantId = message.TenantId,
        MessageId = message.MessageId,
        Sender = message.Sender,
        Recipients = string.Join(';', recipients),
        RawMessage = message.RawMessage,
        SizeBytes = message.SizeBytes,
        Status = message.Status,
        RetryCount = message.RetryCount,
        LastError = message.LastError,
        CreatedUtc = message.CreatedUtc,
        NextRetryUtc = message.NextRetryUtc,
        SourceIp = message.SourceIp,
        AuthenticatedUser = message.AuthenticatedUser,
        DeliveredRecipients = message.DeliveredRecipients,
        FailedRecipients = message.FailedRecipients,
        Origin = message.Origin,
    };

    internal static DateTimeOffset? CalculateNextRetry(int retryCount, DeliveryOptions config)
    {
        if (retryCount <= 0)
            return DateTimeOffset.UtcNow;

        var intervals = config.EffectiveRetryIntervalsMinutes;
        if (intervals.Length == 0)
            return null;

        var intervalIndex = Math.Min(retryCount - 1, intervals.Length - 1);
        var delayMinutes = intervals[intervalIndex];

        // Retry window: the delays up to and including THIS retry (the last interval repeats beyond the array)
        // must stay within MaxRetryHours. Summing the WHOLE interval list instead made every message bounce on
        // its first transient failure whenever the list summed to more than the window (e.g. the default
        // 1,5,30,120,480,1440 = 34.6 h with MaxRetryHours 24).
        var elapsedMinutes = 0L;
        for (var i = 0; i < retryCount; i++)
            elapsedMinutes += intervals[Math.Min(i, intervals.Length - 1)];
        if (elapsedMinutes > config.MaxRetryHours * 60L)
            return null;

        return DateTimeOffset.UtcNow.AddMinutes(delayMinutes);
    }

    private static bool IsBackupMxMessage(IEnumerable<string> recipients, BackupMxSettings backupMx) =>
        recipients
            .Select(r => r[(r.LastIndexOf('@') + 1)..])
            .Any(domain => backupMx.DomainList.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)));
}

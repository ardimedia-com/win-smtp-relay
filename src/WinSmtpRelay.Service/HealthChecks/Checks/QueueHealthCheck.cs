using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// Journal / queue health: the things that show up as "mail isn't going out". Oldest still-queued message
/// age, messages stuck in Delivering, the retry backlog, the 24h bounce rate, a suppression-list spike, and
/// database size / free disk on the DB volume. All host-wide (the runner scope has no active tenant).
/// </summary>
public sealed class QueueHealthCheck(
    RelayDbContext db,
    IMessageQueue queue,
    IOptions<HealthCheckOptions> options,
    ILogger<QueueHealthCheck> logger) : HealthCheckBase
{
    public override string Name => "Queue";
    protected override string Category => HealthCategories.Queue;

    public override async Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct)
    {
        var f = new List<HealthFinding>();
        var opts = options.Value;

        // --- Oldest still-queued message / queue draining ---
        var depth = await queue.GetQueueDepthAsync(ct);
        if (depth == 0)
        {
            f.Add(Ok("queue-drain", "Queue is empty", "No messages are waiting for delivery."));
        }
        else
        {
            // Smallest Id among Queued = the oldest (ordering on Id avoids SQLite's DateTimeOffset ORDER BY limit).
            var oldestUtc = await db.QueuedMessages.AsNoTracking().IgnoreQueryFilters()
                .Where(m => m.Status == MessageStatus.Queued)
                .OrderBy(m => m.Id)
                .Select(m => (DateTimeOffset?)m.CreatedUtc)
                .FirstOrDefaultAsync(ct);

            var age = oldestUtc is { } o ? DateTimeOffset.UtcNow - o : TimeSpan.Zero;
            var ageText = $"{depth} message(s) queued; oldest waiting {FormatAge(age)}";
            if (age >= TimeSpan.FromHours(opts.OldestPendingErrorHours))
                f.Add(Err("queue-drain", "Queue is stuck", $"{ageText}. Mail is not draining — check outbound connectivity and the delivery worker.",
                    hint: "See Connectivity findings and the service log."));
            else if (age >= TimeSpan.FromMinutes(opts.OldestPendingWarningMinutes))
                f.Add(Warn("queue-drain", "Queue is draining slowly", $"{ageText}.", hint: "Watch it — if it keeps growing, check outbound connectivity."));
            else
                f.Add(Ok("queue-drain", "Queue is draining", ageText));
        }

        // --- Messages stuck in Delivering (crash leftovers not yet requeued) ---
        var delivering = await db.QueuedMessages.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(m => m.Status == MessageStatus.Delivering, ct);
        if (delivering > 0)
            f.Add(Warn("stale-delivering", $"{delivering} message(s) stuck in Delivering",
                "These are usually rows stranded by a crash mid-delivery; they're requeued automatically at the next service restart.",
                hint: "If the count persists across a restart, investigate the delivery worker."));

        // --- Retry backlog (many messages with a high retry count = systematic delivery problem) ---
        var retryBacklog = await db.QueuedMessages.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(m => m.Status == MessageStatus.Queued && m.RetryCount >= opts.HighRetryCount, ct);
        if (retryBacklog >= opts.RetryBacklogWarning)
            f.Add(Warn("retry-backlog", $"{retryBacklog} message(s) repeatedly retrying",
                $"{retryBacklog} queued message(s) have retried {opts.HighRetryCount}+ times — a systematic delivery problem (a down smart host, a blocked port, or a bad recipient domain).",
                hint: "Check Connectivity and recent bounces."));

        // --- 24h bounce rate ---
        var (delivered, bounced, deferred) = await CountLast24hAsync(ct);
        var attempts = delivered + bounced + deferred;
        if (attempts >= opts.BounceRateMinAttempts)
        {
            var rate = 100.0 * bounced / attempts;
            if (rate >= opts.BounceRateErrorPercent)
                f.Add(Err("bounce-rate", $"Bounce rate {rate:F0}% in the last 24h",
                    $"{bounced} of {attempts} delivery attempts bounced. A high bounce rate harms reputation and risks blocklisting.",
                    hint: "Check recipient lists, sender domain auth (SPF/DKIM), and for a compromised account."));
            else if (rate >= opts.BounceRateWarningPercent)
                f.Add(Warn("bounce-rate", $"Bounce rate {rate:F0}% in the last 24h",
                    $"{bounced} of {attempts} delivery attempts bounced.", hint: "Watch for invalid recipients or a sender-domain auth problem."));
            else
                f.Add(Ok("bounce-rate", $"Bounce rate {rate:F0}% in the last 24h", $"{delivered} delivered, {bounced} bounced, {deferred} deferred."));
        }

        // --- Suppression-list spike (a list/domain that went bad) ---
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var newSuppressions = await db.SuppressionEntries.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(s => s.CreatedUtc >= since, ct);
        if (newSuppressions >= opts.SuppressionSpikeWarning)
            f.Add(Warn("suppression-spike", $"{newSuppressions} new suppressions in 24h",
                $"{newSuppressions} address(es) were added to the suppression list in the last 24 hours — usually a bad recipient list or a domain that started rejecting.",
                hint: "Review recent bounces and the suppression list."));

        // --- Database size + free disk on the DB volume ---
        f.AddRange(CheckStorage(opts));

        return f;
    }

    private IEnumerable<HealthFinding> CheckStorage(HealthCheckOptions opts)
    {
        string? dbPath;
        try { dbPath = db.Database.GetDbConnection().DataSource; }
        catch (Exception ex) { logger.LogWarning(ex, "could not resolve DB path"); yield break; }

        if (string.IsNullOrWhiteSpace(dbPath))
            yield break;

        var full = Path.GetFullPath(dbPath);
        long sizeBytes = 0;
        try { if (File.Exists(full)) sizeBytes = new FileInfo(full).Length; } catch { /* ignore */ }

        long? freeBytes = null;
        try
        {
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root))
                freeBytes = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) { logger.LogWarning(ex, "could not read free disk space"); }

        var sizeMb = sizeBytes / 1024.0 / 1024.0;
        if (freeBytes is { } free)
        {
            var freeMb = free / 1024.0 / 1024.0;
            var detail = $"Database is {sizeMb:F1} MB; {freeMb:F0} MB free on the database volume.";
            if (freeMb <= opts.LowDiskErrorMb)
                yield return Err("disk", "Very low disk space on the database volume", detail, hint: "Free up disk space — the relay can stop accepting/persisting mail when the volume is full.");
            else if (freeMb <= opts.LowDiskWarningMb)
                yield return Warn("disk", "Low disk space on the database volume", detail, hint: "Free up disk space soon.");
            else
                yield return Ok("disk", "Disk space is healthy", detail);
        }
        else
        {
            yield return Info("disk", "Database size", $"Database is {sizeMb:F1} MB (free disk space could not be read).");
        }
    }

    /// <summary>Delivery-log counts over the last 24h (host-wide). Suppressed skips are excluded from bounces.</summary>
    private async Task<(int delivered, int bounced, int deferred)> CountLast24hAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var logs = await db.DeliveryLogs.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.TimestampUtc >= since)
            .Select(l => new { l.StatusCode, l.StatusMessage })
            .ToListAsync(ct);

        bool IsSuppressed(string? m) => m != null && m.StartsWith("Suppressed", StringComparison.OrdinalIgnoreCase);
        var delivered = logs.Count(l => l.StatusCode.StartsWith('2'));
        var deferred = logs.Count(l => l.StatusCode.StartsWith('4'));
        var bounced = logs.Count(l => l.StatusCode.StartsWith('5') && !IsSuppressed(l.StatusMessage));
        return (delivered, bounced, deferred);
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalHours >= 1 ? $"{age.TotalHours:F1} h"
        : age.TotalMinutes >= 1 ? $"{age.TotalMinutes:F0} min"
        : $"{age.TotalSeconds:F0} s";
}

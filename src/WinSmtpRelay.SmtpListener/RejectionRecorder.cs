using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.SmtpListener;

/// <summary>Records a refused submission. Implementations MUST be non-blocking: this runs in an SMTP session.</summary>
public interface IRejectionRecorder
{
    /// <summary>
    /// Note one rejection. Returns immediately — the value is folded into an in-memory aggregate and
    /// written to the database by a background flush. Never throws.
    /// </summary>
    void Record(
        string? clientIp,
        RejectReason reason,
        int replyCode,
        string? senderDomain = null,
        int? tenantId = null,
        string? detail = null,
        string? rawBuffer = null);
}

/// <summary>
/// Aggregates refused submissions in memory and flushes them to the database periodically.
/// <para>
/// The store is an aggregate, not a log, so this is deliberately NOT a write-batching queue: the hot
/// path folds the rejection into a <see cref="ConcurrentDictionary"/> keyed exactly like the table
/// (no await, no I/O, no allocation per event beyond the first of its kind), and a background loop
/// upserts the folded counts every few seconds. A scanner storm therefore hits RAM, not SQLite, and
/// the database write rate stays bounded by the flush interval no matter how fast rejections arrive.
/// Losing a few seconds of counts on a crash is acceptable for a statistics table; failing an SMTP
/// session to persist one is not.
/// </para>
/// </summary>
public sealed class RejectionRecorder(
    IServiceScopeFactory scopeFactory,
    IOptions<SmtpListenerOptions> options,
    ILogger<RejectionRecorder> logger) : BackgroundService, IRejectionRecorder
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ceiling on DISTINCT pending keys between flushes. A single /16 scan can mint tens of thousands
    /// of distinct client IPs in seconds; without this the aggregate would be an in-memory growth
    /// vector, which is the same defect as the on-disk one requirement 5 guards against.
    /// </summary>
    private const int MaxPendingKeys = 5_000;

    // Row caps per trust partition. Partitioned on purpose: untrusted rows are minted by anyone who can
    // reach port 25, so a single cap would let scanner churn evict exactly the trusted-network findings
    // this feature exists to surface. Untrusted pressure can only ever evict untrusted rows.
    private const int MaxTrustedRows = 2_000;
    private const int MaxUntrustedRows = 2_000;

    private readonly SmtpListenerOptions _options = options.Value;
    private ConcurrentDictionary<RejectionKey, PendingAggregate> _pending = new();
    private long _droppedSinceLastFlush;

    public void Record(
        string? clientIp,
        RejectReason reason,
        int replyCode,
        string? senderDomain = null,
        int? tenantId = null,
        string? detail = null,
        string? rawBuffer = null)
    {
        try
        {
            // A rejection whose client we cannot name is not actionable and cannot be aggregated.
            if (string.IsNullOrWhiteSpace(clientIp))
                return;

            var key = new RejectionKey(
                clientIp,
                reason,
                replyCode,
                // "" not null: it is part of the table's unique key, where NULLs would compare distinct.
                NormalizeDomain(senderDomain));

            var pending = _pending;
            if (!pending.TryGetValue(key, out var aggregate))
            {
                if (pending.Count >= MaxPendingKeys)
                {
                    Interlocked.Increment(ref _droppedSinceLastFlush);
                    return;
                }

                aggregate = pending.GetOrAdd(key, _ => new PendingAggregate());
            }

            lock (aggregate)
            {
                var now = DateTimeOffset.UtcNow;
                if (aggregate.Count == 0)
                    aggregate.FirstSeenUtc = now;
                aggregate.Count++;
                aggregate.LastSeenUtc = now;
                aggregate.TenantId = tenantId ?? aggregate.TenantId;
                aggregate.Detail = detail ?? aggregate.Detail;
                aggregate.Buffer = rawBuffer ?? aggregate.Buffer;
            }
        }
        catch (Exception ex)
        {
            // Recording is observability, never a reason to fail a session.
            logger.LogDebug(ex, "Recording a rejection failed");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Rejection recorder starting (flush every {Seconds}s)", FlushInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, stoppingToken);
                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Flushing rejections failed; counts for this interval are lost");
            }
        }

        // Best-effort final flush so a graceful stop does not discard the last interval.
        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Final rejection flush failed");
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var snapshot = Interlocked.Exchange(ref _pending, new ConcurrentDictionary<RejectionKey, PendingAggregate>());

        var dropped = Interlocked.Exchange(ref _droppedSinceLastFlush, 0);
        if (dropped > 0)
        {
            logger.LogWarning(
                "Rejection recorder dropped {Dropped} rejection(s) this interval: more than {Max} distinct "
                + "client/reason combinations were pending. Counts are understated for this interval.",
                dropped, MaxPendingKeys);
        }

        if (snapshot.IsEmpty)
            return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var configCache = scope.ServiceProvider.GetRequiredService<IRuntimeConfigCache>();

        var ipRules = await configCache.GetIpAccessRulesAsync(ct);

        // One query for the whole batch rather than one per key.
        var clientIps = snapshot.Keys.Select(k => k.ClientIp).Distinct().ToList();
        var existingRows = await db.RejectedSubmissions
            .Where(r => clientIps.Contains(r.ClientIp))
            .ToListAsync(ct);

        var existingByKey = existingRows.ToDictionary(
            r => new RejectionKey(r.ClientIp, r.Reason, r.ReplyCode, r.SenderDomain));

        var trustByIp = new Dictionary<string, bool>();
        var inserted = false;

        foreach (var (key, aggregate) in snapshot)
        {
            long count;
            DateTimeOffset firstSeen, lastSeen;
            int? tenantId;
            string? detail, buffer;
            lock (aggregate)
            {
                count = aggregate.Count;
                firstSeen = aggregate.FirstSeenUtc;
                lastSeen = aggregate.LastSeenUtc;
                tenantId = aggregate.TenantId;
                detail = aggregate.Detail;
                buffer = aggregate.Buffer;
            }

            if (count == 0)
                continue;

            if (!trustByIp.TryGetValue(key.ClientIp, out var isTrusted))
            {
                isTrusted = EvaluateTrust(key.ClientIp, ipRules);
                trustByIp[key.ClientIp] = isTrusted;
            }

            if (existingByKey.TryGetValue(key, out var row))
            {
                // A row that reappears after a long silence is a NEW episode: keeping the original
                // FirstSeenUtc would make a device that was fixed and broke again weeks later look like
                // it had been failing continuously, and trip the "failing for over a day" finding instantly.
                if (lastSeen - row.LastSeenUtc > RejectedSubmission.EpisodeResetWindow)
                    row.FirstSeenUtc = firstSeen;

                row.Count += count;
                row.LastSeenUtc = lastSeen;
                // Re-stamped only on rows that are actively rejecting. Untouched history is never
                // reclassified by a later rule edit.
                row.IsTrustedSource = isTrusted;
                row.TenantId = tenantId ?? row.TenantId;
                row.Detail = detail ?? row.Detail;
                row.LastBuffer = buffer ?? row.LastBuffer;
            }
            else
            {
                db.RejectedSubmissions.Add(new RejectedSubmission
                {
                    ClientIp = key.ClientIp,
                    Reason = key.Reason,
                    ReplyCode = key.ReplyCode,
                    SenderDomain = key.SenderDomain,
                    TenantId = tenantId,
                    Detail = detail,
                    LastBuffer = buffer,
                    IsTrustedSource = isTrusted,
                    Count = count,
                    FirstSeenUtc = firstSeen,
                    LastSeenUtc = lastSeen
                });
                inserted = true;
            }
        }

        await db.SaveChangesAsync(ct);

        if (inserted)
        {
            await EnforceRowCapAsync(db, trusted: true, MaxTrustedRows, ct);
            await EnforceRowCapAsync(db, trusted: false, MaxUntrustedRows, ct);
        }
    }

    private bool EvaluateTrust(string clientIp, IReadOnlyList<IpAccessRule> ipRules)
    {
        return System.Net.IPAddress.TryParse(clientIp, out var address)
            && IpAccessEvaluator.IsTrustedSource(address, ipRules, _options.AllowedNetworks);
    }

    /// <summary>
    /// Evicts the least-recently-seen rows of ONE trust partition. Called per partition so untrusted
    /// churn can never displace a trusted-network finding.
    /// </summary>
    private async Task EnforceRowCapAsync(RelayDbContext db, bool trusted, int max, CancellationToken ct)
    {
        var count = await db.RejectedSubmissions.CountAsync(r => r.IsTrustedSource == trusted, ct);
        if (count <= max)
            return;

        // LastSeenUtc is stored as a fixed-width UTC ISO-8601 string, so lexicographic order is
        // chronological and SQLite can ORDER BY it (see RelayDbContext.ConfigureConventions).
        var doomed = await db.RejectedSubmissions
            .Where(r => r.IsTrustedSource == trusted)
            .OrderBy(r => r.LastSeenUtc)
            .Take(count - max)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var evicted = await db.RejectedSubmissions
            .Where(r => doomed.Contains(r.Id))
            .ExecuteDeleteAsync(ct);

        logger.LogInformation(
            "Evicted {Evicted} least-recent {Partition} rejection row(s) to stay within the cap of {Max}",
            evicted, trusted ? "trusted" : "untrusted", max);
    }

    private static string NormalizeDomain(string? domain) =>
        string.IsNullOrWhiteSpace(domain) ? "" : domain.Trim().ToLowerInvariant();

    /// <summary>The table's unique key, mirrored in memory so the fold and the upsert agree by construction.</summary>
    private readonly record struct RejectionKey(string ClientIp, RejectReason Reason, int ReplyCode, string SenderDomain);

    private sealed class PendingAggregate
    {
        public long Count;
        public DateTimeOffset FirstSeenUtc;
        public DateTimeOffset LastSeenUtc;
        public int? TenantId;
        public string? Detail;
        public string? Buffer;
    }
}

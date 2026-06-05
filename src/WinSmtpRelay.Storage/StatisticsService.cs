using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class StatisticsService(RelayDbContext db) : IStatisticsService
{
    public async Task<IReadOnlyList<TimeBucketResult>> GetLiveStatisticsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        var now = DateTime.UtcNow;

        var logs = await db.DeliveryLogs
            .AsNoTracking()
            .Where(l => l.TimestampUtc >= cutoff)
            .Select(l => new { l.TimestampUtc, l.StatusCode })
            .ToListAsync(ct);

        var buckets = new TimeBucketResult[60];
        for (var i = 0; i < 60; i++)
        {
            var second = now.AddSeconds(-(59 - i));
            var key = second.Second;
            var matching = logs.Where(l => (int)(now - l.TimestampUtc).TotalSeconds == 59 - i);
            buckets[i] = new TimeBucketResult(
                second.ToString("HH:mm:ss"),
                matching.Count(l => l.StatusCode == "250"),
                matching.Count(l => l.StatusCode.StartsWith('5')),
                second);
        }

        return buckets;
    }

    public async Task<IReadOnlyList<TimeBucketResult>> GetHourlyStatisticsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var now = DateTime.UtcNow;

        var logs = await db.DeliveryLogs
            .AsNoTracking()
            .Where(l => l.TimestampUtc >= cutoff)
            .Select(l => new { l.TimestampUtc, l.StatusCode })
            .ToListAsync(ct);

        var buckets = new TimeBucketResult[60];
        for (var i = 0; i < 60; i++)
        {
            var minute = now.AddMinutes(-(59 - i));
            var matching = logs.Where(l => (int)(now - l.TimestampUtc).TotalMinutes >= 59 - i
                                        && (int)(now - l.TimestampUtc).TotalMinutes < 60 - i);
            buckets[i] = new TimeBucketResult(
                minute.ToString("HH:mm"),
                matching.Count(l => l.StatusCode == "250"),
                matching.Count(l => l.StatusCode.StartsWith('5')),
                minute);
        }

        return buckets;
    }

    public async Task<IReadOnlyList<TimeBucketResult>> GetDailyStatisticsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var now = DateTime.UtcNow;

        var grouped = await db.DeliveryLogs
            .AsNoTracking()
            .Where(l => l.TimestampUtc >= cutoff)
            .GroupBy(l => l.TimestampUtc.Hour)
            .Select(g => new
            {
                Hour = g.Key,
                Sent = g.Count(l => l.StatusCode == "250"),
                Failed = g.Count(l => l.StatusCode.StartsWith("5"))
            })
            .ToListAsync(ct);

        var buckets = new TimeBucketResult[24];
        for (var i = 0; i < 24; i++)
        {
            var hour = now.AddHours(-(23 - i));
            var hourKey = hour.Hour;
            var match = grouped.FirstOrDefault(g => g.Hour == hourKey);
            buckets[i] = new TimeBucketResult(
                hour.ToString("HH:00"),
                match?.Sent ?? 0,
                match?.Failed ?? 0,
                hour);
        }

        return buckets;
    }

    public async Task<IReadOnlyList<DailyBucketResult>> GetMonthlyStatisticsAsync(CancellationToken ct = default)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);

        // Sum per date so the result holds whether the caller is scoped to one tenant (its own
        // rows) or unscoped/host with "All tenants" selected (the query filter is off, so multiple
        // tenant rows per date are summed into the host-wide total).
        var rows = await db.DailyStatistics
            .AsNoTracking()
            .Where(d => d.Date >= cutoff)
            .GroupBy(d => d.Date)
            .Select(g => new
            {
                Date = g.Key,
                Sent = g.Sum(x => x.TotalSent),
                Failed = g.Sum(x => x.TotalFailed),
                Bounced = g.Sum(x => x.TotalBounced)
            })
            .ToListAsync(ct);

        // Zero-fill missing days
        var results = new List<DailyBucketResult>();
        for (var i = 0; i < 30; i++)
        {
            var date = cutoff.AddDays(i + 1);
            var match = rows.FirstOrDefault(r => r.Date == date);
            results.Add(new DailyBucketResult(
                date.ToString("yyyy-MM-dd"),
                match?.Sent ?? 0,
                match?.Failed ?? 0,
                match?.Bounced ?? 0));
        }

        return results;
    }

    public async Task AggregateDayAsync(DateOnly date, CancellationToken ct = default)
    {
        var startUtc = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endUtc = startUtc.AddDays(1);

        // Runs unscoped from the background aggregator, so the query filter is off and this sees
        // every tenant's logs; rows are then aggregated per tenant.
        var logs = await db.DeliveryLogs
            .AsNoTracking()
            .Where(l => l.TimestampUtc >= startUtc && l.TimestampUtc < endUtc)
            .Select(l => new { l.TenantId, l.StatusCode, l.QueuedMessageId, l.TimestampUtc })
            .ToListAsync(ct);

        // Load message creation times once for the day to compute delivery latency.
        var messageIds = logs.Select(l => l.QueuedMessageId).Distinct().ToList();
        var createdById = messageIds.Count == 0
            ? new Dictionary<long, DateTime>()
            : (await db.QueuedMessages
                .AsNoTracking()
                .Where(m => messageIds.Contains(m.Id))
                .Select(m => new { m.Id, m.CreatedUtc })
                .ToListAsync(ct))
                .ToDictionary(m => m.Id, m => m.CreatedUtc);

        var now = DateTime.UtcNow;
        foreach (var group in logs.GroupBy(l => l.TenantId))
        {
            var tenantId = group.Key;
            var sent = group.Count(l => l.StatusCode == "250");
            var failed = group.Count(l => l.StatusCode.StartsWith('5'));
            var bounced = group.Count(l => l.StatusCode.StartsWith('4'));

            var deliveryTimes = group
                .Select(l => createdById.TryGetValue(l.QueuedMessageId, out var created)
                    ? (l.TimestampUtc - created).TotalMilliseconds
                    : 0)
                .Where(ms => ms > 0)
                .ToList();
            var avgDeliveryMs = deliveryTimes.Count > 0 ? deliveryTimes.Average() : 0.0;

            var existing = await db.DailyStatistics.FindAsync([tenantId, date], ct);
            if (existing is not null)
            {
                existing.TotalSent = sent;
                existing.TotalFailed = failed;
                existing.TotalBounced = bounced;
                existing.AverageDeliveryTimeMs = avgDeliveryMs;
                existing.ComputedAtUtc = now;
            }
            else
            {
                db.DailyStatistics.Add(new DailyStatistics
                {
                    TenantId = tenantId,
                    Date = date,
                    TotalSent = sent,
                    TotalFailed = failed,
                    TotalBounced = bounced,
                    AverageDeliveryTimeMs = avgDeliveryMs,
                    ComputedAtUtc = now
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task BackfillAsync(CancellationToken ct = default)
    {
        var earliest = await db.DeliveryLogs
            .AsNoTracking()
            .OrderBy(l => l.TimestampUtc)
            .Select(l => (DateTime?)l.TimestampUtc)
            .FirstOrDefaultAsync(ct);

        if (earliest is null)
            return;

        var startDate = DateOnly.FromDateTime(earliest.Value);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        for (var date = startDate; date <= yesterday; date = date.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            await AggregateDayAsync(date, ct);
        }
    }

    public async Task PurgeOldStatisticsAsync(int retentionDays, CancellationToken ct = default)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-retentionDays);
        await db.DailyStatistics
            .Where(d => d.Date < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}

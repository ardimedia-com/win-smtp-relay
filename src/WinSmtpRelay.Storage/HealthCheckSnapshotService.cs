using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Stores and reads daily self-check snapshots. Host-level: every query ignores the tenant filter (a
/// snapshot is a whole-relay record with no TenantId). Ordering is by the autoincrement <c>Id</c>, which is
/// chronological by construction, so it works on SQLite without ordering on the converted DateTimeOffset
/// column.
/// </summary>
public class HealthCheckSnapshotService(RelayDbContext db) : IHealthCheckSnapshotService
{
    public async Task SaveAsync(HealthCheckSnapshot snapshot, CancellationToken ct = default)
    {
        db.HealthCheckSnapshots.Add(snapshot); // findings are inserted with it via the navigation
        await db.SaveChangesAsync(ct);
    }

    public async Task<HealthCheckSnapshot?> GetLatestAsync(CancellationToken ct = default)
        => await db.HealthCheckSnapshots
            .AsNoTracking()
            .Include(s => s.Findings)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<HealthCheckSnapshot>> GetHistoryAsync(int maxCount, CancellationToken ct = default)
        => await db.HealthCheckSnapshots
            .AsNoTracking()
            .OrderByDescending(s => s.Id)
            .Take(maxCount)
            // Counts only — the history/trend view doesn't need every finding row.
            .Select(s => new HealthCheckSnapshot
            {
                Id = s.Id,
                RunUtc = s.RunUtc,
                DurationMs = s.DurationMs,
                ErrorCount = s.ErrorCount,
                WarningCount = s.WarningCount,
                InfoCount = s.InfoCount,
                OkCount = s.OkCount,
            })
            .ToListAsync(ct);

    public async Task PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
        // Findings are removed by the DB-level ON DELETE CASCADE on the snapshot FK.
        => await db.HealthCheckSnapshots.Where(s => s.RunUtc < cutoffUtc).ExecuteDeleteAsync(ct);
}

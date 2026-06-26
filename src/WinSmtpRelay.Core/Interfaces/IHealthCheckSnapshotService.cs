using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Persists and reads daily self-check snapshots. Host-level (not tenant-scoped): a snapshot is a
/// whole-relay health record. The interface lives in Core so the Blazor admin UI can resolve it without
/// referencing the storage implementation.
/// </summary>
public interface IHealthCheckSnapshotService
{
    /// <summary>Stores a completed run with its findings and per-severity counts.</summary>
    Task SaveAsync(HealthCheckSnapshot snapshot, CancellationToken ct = default);

    /// <summary>The most recent snapshot including its findings, or null if no run has happened yet.</summary>
    Task<HealthCheckSnapshot?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>Recent snapshots (counts only, newest first) for the history/trend view.</summary>
    Task<IReadOnlyList<HealthCheckSnapshot>> GetHistoryAsync(int maxCount, CancellationToken ct = default);

    /// <summary>Deletes snapshots (and their findings) older than the cutoff.</summary>
    Task PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);
}

using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Runs every registered self-check, aggregates the findings into a <see cref="HealthCheckSnapshot"/>, and
/// persists it. The interface lives in Core so the admin UI can trigger an on-demand run ("Run now") without
/// referencing the host project that implements the checks. The background service uses the same entry point
/// for the scheduled daily run.
/// </summary>
public interface IHealthCheckRunner
{
    /// <summary>Runs all checks, saves the snapshot, prunes old snapshots, and returns the new snapshot.</summary>
    Task<HealthCheckSnapshot> RunAndSaveAsync(CancellationToken ct = default);
}

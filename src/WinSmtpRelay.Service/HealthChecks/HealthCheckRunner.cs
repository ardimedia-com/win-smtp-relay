using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Service.HealthChecks;

/// <summary>
/// Runs every registered <see cref="IHealthCheck"/>, aggregates the findings into a
/// <see cref="HealthCheckSnapshot"/>, persists it, and prunes old snapshots. Always runs in a fresh,
/// unscoped DI scope so the run is host-wide (all tenants) regardless of who triggered it — the daily
/// background run and the admin UI's "Run now" button behave identically. A check that throws becomes a
/// single Warning finding, so one broken probe never aborts the run.
/// </summary>
public class HealthCheckRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<HealthCheckOptions> options,
    ILogger<HealthCheckRunner> logger) : IHealthCheckRunner
{
    public async Task<HealthCheckSnapshot> RunAndSaveAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        var sw = Stopwatch.StartNew();

        // Fresh scope with no active tenant ⇒ the EF tenant filter is off, so the checks see every
        // tenant's domains/queue/suppressions. The checks are scoped + (some) stateful, so they share
        // this single scope and run sequentially — a daily job has no need for the page's concurrency.
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var findings = new List<HealthFinding>();
        foreach (var check in sp.GetServices<IHealthCheck>())
        {
            using var perCheck = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // A generous per-check ceiling so a single hung network probe can't stall the whole run.
            perCheck.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, opts.ProbeTimeoutSeconds) * 5));
            try
            {
                findings.AddRange(await check.RunAsync(perCheck.Token));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // host is shutting down
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Self-check '{Check}' failed", check.Name);
                findings.Add(new HealthFinding(
                    HealthCategories.Runtime, $"check-failed:{check.Name}", HealthSeverity.Warning,
                    $"The '{check.Name}' self-check could not run",
                    $"This check threw an error and was skipped, so its area was not verified this run: {ex.Message}"));
            }
        }

        sw.Stop();

        var snapshot = new HealthCheckSnapshot
        {
            RunUtc = DateTimeOffset.UtcNow,
            DurationMs = (int)sw.ElapsedMilliseconds,
            ErrorCount = findings.Count(f => f.Severity == HealthSeverity.Error),
            WarningCount = findings.Count(f => f.Severity == HealthSeverity.Warning),
            InfoCount = findings.Count(f => f.Severity == HealthSeverity.Info),
            OkCount = findings.Count(f => f.Severity == HealthSeverity.Ok),
            Findings = [.. OrderForDisplay(findings).Select(f => new HealthCheckFinding
            {
                Category = f.Category,
                Code = f.Code,
                Severity = f.Severity,
                Title = f.Title,
                Detail = f.Detail,
                Target = f.Target,
                Hint = f.Hint,
            })],
        };

        var snapshots = sp.GetRequiredService<IHealthCheckSnapshotService>();
        await snapshots.SaveAsync(snapshot, ct);

        if (opts.RetentionDays > 0)
        {
            try { await snapshots.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-opts.RetentionDays), ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Pruning old self-check snapshots failed"); }
        }

        logger.LogInformation(
            "Self-check complete: {Errors} error(s), {Warnings} warning(s), {Oks} ok in {Ms} ms",
            snapshot.ErrorCount, snapshot.WarningCount, snapshot.OkCount, snapshot.DurationMs);
        return snapshot;
    }

    // Persist findings worst-first, then in category display order, so the UI and digest render them
    // straight from storage without re-sorting.
    private static IEnumerable<HealthFinding> OrderForDisplay(IEnumerable<HealthFinding> findings) =>
        findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => CategoryRank(f.Category))
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase);

    private static int CategoryRank(string category)
    {
        var idx = HealthCategories.Display.ToList().IndexOf(category);
        return idx < 0 ? int.MaxValue : idx;
    }
}

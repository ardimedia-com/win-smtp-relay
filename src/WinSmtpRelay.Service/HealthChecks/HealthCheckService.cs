using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Mail;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Service.HealthChecks;

/// <summary>
/// Runs the daily self-check on a schedule (and once shortly after startup — when the network and DNS answer —
/// so the page/digest always have data), then — if a NEW blocking finding appeared since the previous run — sends an immediate alert through
/// the relay's own pipeline to the configured reporting recipient. The daily digest itself carries the full
/// summary; this service exists so a brand-new blocking problem doesn't wait until the next digest.
/// <para>
/// The run time is a local time of day from Settings → Reporting (default: 30 minutes before the digest), read
/// on every wake-up so a change applies without a restart; the next run is computed in UTC, DST-safe.
/// </para>
/// </summary>
public class HealthCheckService(
    IServiceScopeFactory scopeFactory,
    IHealthCheckRunner runner,
    IOptions<HealthCheckOptions> options,
    ILogger<HealthCheckService> logger) : BackgroundService
{
    // How often to wake while waiting for the next scheduled run (keeps shutdown responsive).
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    // In-memory cooldown for the immediate alert (reset on restart — acceptable for an alert heuristic;
    // the "is this finding NEW?" check is against the persisted previous snapshot, so a restart alone
    // never re-alerts).
    private DateTimeOffset _lastAlertUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Self-check service disabled (HealthCheck:Enabled=false)");
            return;
        }

        logger.LogInformation("Self-check service starting");

        // Initial run a short while after startup: populates the page/digest and verifies a fresh install,
        // without competing with the rest of startup — and only once the network and DNS answer (bounded), so a
        // start right after boot or wake-up does not report every smart host as unreachable.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            var ready = await StartupGate.WaitUntilReadyAsync(NetworkReadyAsync,
                TimeSpan.FromMinutes(Math.Max(0, opts.StartupNetworkWaitMinutes)), TimeSpan.FromSeconds(15), stoppingToken);
            if (!ready)
                logger.LogWarning("Self-check: network/DNS still not ready after {Minutes} min — running anyway", opts.StartupNetworkWaitMinutes);
        }
        catch (OperationCanceledException) { return; }
        await RunOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await WaitForNextRunAsync(stoppingToken))
                break;
            await RunOnceAsync(stoppingToken);
        }
    }

    // Network up + a public name resolves through the system resolver.
    private async Task<bool> NetworkReadyAsync(CancellationToken ct)
    {
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            return false;
        var probeHost = string.IsNullOrWhiteSpace(options.Value.Outbound25TestDomain) ? "gmail.com" : options.Value.Outbound25TestDomain.Trim();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var addresses = await System.Net.Dns.GetHostAddressesAsync(probeHost, cts.Token);
        return addresses.Length > 0;
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            // Capture the previous (persisted) snapshot before the new run, to diff for newly-appeared errors.
            HealthCheckSnapshot? previous;
            using (var scope = scopeFactory.CreateScope())
                previous = await scope.ServiceProvider
                    .GetRequiredService<IHealthCheckSnapshotService>().GetLatestAsync(ct);

            var snapshot = await runner.RunAndSaveAsync(ct);
            await MaybeAlertOnNewErrorsAsync(previous, snapshot, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Self-check run failed");
        }
    }

    private async Task MaybeAlertOnNewErrorsAsync(HealthCheckSnapshot? previous, HealthCheckSnapshot current, CancellationToken ct)
    {
        var previousErrorKeys = previous is null
            ? []
            : previous.Findings.Where(f => f.Severity == HealthSeverity.Error).Select(Key).ToHashSet();

        var newErrors = current.Findings
            .Where(f => f.Severity == HealthSeverity.Error && !previousErrorKeys.Contains(Key(f)))
            .ToList();
        if (newErrors.Count == 0)
            return;

        var opts = options.Value;
        if (DateTimeOffset.UtcNow - _lastAlertUtc < TimeSpan.FromHours(Math.Max(0, opts.AlertCooldownHours)))
        {
            logger.LogInformation("Self-check: {Count} new blocking finding(s), but within the alert cooldown — not alerting", newErrors.Count);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var reporting = await sp.GetRequiredService<IReportingSettingsService>().GetAsync(ct);
        if (!reporting.Enabled || string.IsNullOrWhiteSpace(reporting.RecipientAddress))
            return; // alerts ride on the reporting recipient; nothing to send to

        var from = reporting.FromAddress;
        if (string.IsNullOrWhiteSpace(from))
            from = (await sp.GetRequiredService<IPortalSettingsService>().GetAsync(ct)).SignupFromAddress;
        if (string.IsNullOrWhiteSpace(from))
        {
            logger.LogWarning("Self-check found new blocking finding(s) but no from-address is configured — alert skipped");
            return;
        }

        var queue = sp.GetRequiredService<IMessageQueue>();
        await SystemEmail.EnqueueAsync(queue, from, reporting.RecipientAddress!.Trim(),
            $"WIN-SMTP-RELAY ALERT: {newErrors.Count} new setup/health problem(s) — {Environment.MachineName}",
            BuildAlertContent(newErrors), TenantDefaults.DefaultTenantId, ct);

        _lastAlertUtc = DateTimeOffset.UtcNow;
        logger.LogWarning("Self-check: {Count} new blocking finding(s) — alert sent to {Recipient}", newErrors.Count, reporting.RecipientAddress);
    }

    private static SystemEmailContent BuildAlertContent(IReadOnlyList<HealthCheckFinding> newErrors)
    {
        var sb = new StringBuilder();
        foreach (var f in newErrors)
        {
            sb.Append("• ").Append(f.Title);
            if (!string.IsNullOrWhiteSpace(f.Target))
                sb.Append("  [").Append(f.Target).Append(']');
            sb.AppendLine();
            sb.Append("    ").AppendLine(f.Detail);
            if (!string.IsNullOrWhiteSpace(f.Hint))
                sb.Append("    → ").AppendLine(f.Hint);
            sb.AppendLine();
        }

        return new SystemEmailContent
        {
            Title = $"{newErrors.Count} new setup / health problem(s) detected",
            Paragraphs =
            [
                $"The daily self-check on {Environment.MachineName} found {newErrors.Count} new blocking problem(s) " +
                "that were not present in the previous run. These can stop mail from being delivered or accepted:",
            ],
            MonospaceBlock = sb.ToString(),
            ClosingParagraphs = ["Open Diagnostics in the admin UI for the full report and remediation steps."],
            FooterNote = "Sent by WIN-SMTP-RELAY self-check — configure the recipient under Settings → Reporting.",
        };
    }

    private static string Key(HealthCheckFinding f) => $"{f.Code}|{f.Target}";

    /// <summary>
    /// Sleeps (in capped chunks, for responsive shutdown) until the next configured run time. The target is
    /// measured from the moment the wait STARTED — re-computing it from "now" on every wake-up would always yield
    /// tomorrow once the target minute is reached, so the scheduled run never happened (only the startup run
    /// did). The configured time is re-read on every wake-up, so a change under Settings → Reporting applies to
    /// the current wait.
    /// </summary>
    private async Task<bool> WaitForNextRunAsync(CancellationToken ct)
    {
        var waitStartedUtc = DateTime.UtcNow;
        string? logged = null;
        while (!ct.IsCancellationRequested)
        {
            ReportSchedule schedule;
            try { schedule = await GetScheduleAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Self-check: reading the schedule failed — using the default");
                schedule = ReportSchedule.From(new ReportingSettings());
            }

            var nextRun = schedule.NextSelfCheckUtc(waitStartedUtc);
            var wait = nextRun - DateTime.UtcNow;
            if (wait <= TimeSpan.Zero)
                return true;

            var label = schedule.Label(schedule.SelfCheckTime);
            if (logged != label)
            {
                logger.LogInformation("Next self-check at {Time} = {Utc:yyyy-MM-dd HH:mm} UTC (in ~{Delay})", label, nextRun, wait);
                logged = label;
            }

            try { await Task.Delay(wait < PollInterval ? wait : PollInterval, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private async Task<ReportSchedule> GetScheduleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IReportingSettingsService>().GetAsync(ct);
        return ReportSchedule.From(settings);
    }
}

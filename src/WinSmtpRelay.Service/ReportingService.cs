using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Mail;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Service;

/// <summary>
/// Sends a daily activity digest and immediate alerts on important incidents (a sending IP getting
/// blocklisted, an elevated bounce rate) to the configured report address, through the relay's own
/// delivery pipeline. Host-level; reads <see cref="ReportingSettings"/> each cycle so changes apply
/// without a restart. Disabled until enabled (with a recipient) on the Settings page.
/// </summary>
public class ReportingService(
    IServiceScopeFactory scopeFactory,
    ILogger<ReportingService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DnsblAlertCooldown = TimeSpan.FromHours(12);
    private static readonly TimeSpan BounceAlertCooldown = TimeSpan.FromHours(6);
    private const int MinAttemptsForBounceAlert = 20;
    private const int MaxNewSuppressionsListed = 100;

    /// <summary>Cap on refused-submission rows listed in the digest, so one noisy subnet can't swamp the report.</summary>
    private const int MaxRejectionsListed = 20;

    // In-memory de-bounce of incident alerts (reset on restart — acceptable for an alerting heuristic).
    private readonly Dictionary<string, DateTimeOffset> _lastAlert = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Reporting service starting");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reporting cycle failed");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var settings = await sp.GetRequiredService<IReportingSettingsService>().GetAsync(ct);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.RecipientAddress))
            return;

        // From-address: explicit reporting from-address, else the signup/system from-address.
        var from = settings.FromAddress;
        if (string.IsNullOrWhiteSpace(from))
            from = (await sp.GetRequiredService<IPortalSettingsService>().GetAsync(ct)).SignupFromAddress;
        if (string.IsNullOrWhiteSpace(from))
        {
            logger.LogWarning("Reporting is enabled but no from-address is configured (Reporting or Signup). Skipping.");
            return;
        }

        var to = settings.RecipientAddress!.Trim();
        var queue = sp.GetRequiredService<IMessageQueue>();

        // ---- Incident alerts (every cycle, de-bounced) ----
        await CheckBlocklistIncidentsAsync(sp, queue, from, to, ct);
        await CheckBounceRateIncidentAsync(sp, queue, from, to, settings.BounceRateAlertPercent, ct);

        // ---- Daily digest (once per UTC day, at/after the configured time) ----
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (settings.LastDigestSentDate != today &&
            TimeOnly.TryParse(settings.DailyTimeUtc, out var sendAt) &&
            TimeOnly.FromDateTime(DateTime.UtcNow) >= sendAt)
        {
            // Capture the "since last report" cutoff from the CURRENT settings (still the previous send
            // moment) before MarkDigestSentAsync advances it.
            var content = await BuildDigestAsync(sp, SuppressionReportCutoff(settings), ct);
            await SendAsync(queue, from, to, $"WIN-SMTP-RELAY daily report — {today:yyyy-MM-dd} — {Environment.MachineName}", content, ct);
            await sp.GetRequiredService<IReportingSettingsService>().MarkDigestSentAsync(today, ct);
            logger.LogInformation("Daily report sent to {Recipient}", to);
        }
    }

    private async Task CheckBlocklistIncidentsAsync(IServiceProvider sp, IMessageQueue queue, string from, string to, CancellationToken ct)
    {
        var dnsSettings = await sp.GetRequiredService<IDnsSettingsService>().GetAsync(ct);
        var ips = SplitList(dnsSettings.SendingIpAddresses);
        if (ips.Count == 0)
            return;

        var dns = sp.GetRequiredService<IDnsSetupService>();
        foreach (var ip in ips)
        {
            var result = await dns.CheckBlocklistsAsync(ip, ct);
            if (result.Status != DnsRecordStatus.Listed)
                continue;

            if (!ShouldAlert($"dnsbl:{ip}", DnsblAlertCooldown))
                continue;

            await SendAsync(queue, from, to,
                $"WIN-SMTP-RELAY ALERT: sending IP {ip} is blocklisted",
                new SystemEmailContent
                {
                    Title = $"Sending IP {ip} is blocklisted",
                    Paragraphs = [$"The sending IP {ip} appears on a DNS blocklist (DNSBL):"],
                    MonospaceBlock = result.Explanation,
                    ClosingParagraphs =
                    [
                        "Mail from this IP will be rejected or spam-foldered by many providers. Find and stop the cause " +
                        "(spam from a compromised account, misconfiguration), then request delisting at the listing provider. " +
                        "Consider relaying outbound mail through a reputable smart host. See the Health page for details.",
                    ],
                },
                ct);
            logger.LogWarning("Reporting: sending IP {Ip} is blocklisted — alert sent", ip);
        }
    }

    private async Task CheckBounceRateIncidentAsync(IServiceProvider sp, IMessageQueue queue, string from, string to, int thresholdPercent, CancellationToken ct)
    {
        if (thresholdPercent <= 0)
            return;

        var (delivered, bounced, deferred, _) = await CountLast24hAsync(sp, ct);
        var attempts = delivered + bounced + deferred;
        if (attempts < MinAttemptsForBounceAlert)
            return;

        var rate = 100.0 * bounced / attempts;
        if (rate < thresholdPercent)
            return;

        if (!ShouldAlert("bouncerate", BounceAlertCooldown))
            return;

        await SendAsync(queue, from, to,
            $"WIN-SMTP-RELAY ALERT: bounce rate {rate:F0}% over the last 24h",
            new SystemEmailContent
            {
                Title = $"Bounce rate {rate:F0}% over the last 24 hours",
                Paragraphs =
                [
                    $"The outbound bounce rate over the last 24 hours is {rate:F1}% ({bounced} bounced of {attempts} attempts), " +
                    $"above the {thresholdPercent}% alert threshold.",
                    "A high bounce rate harms sending reputation and can lead to blocklisting. Check for invalid " +
                    "recipient lists, a misconfigured sender domain (SPF/DKIM), or a compromised account. The " +
                    "suppression list already stops repeat delivery to hard-bounced addresses.",
                ],
            },
            ct);
        logger.LogWarning("Reporting: 24h bounce rate {Rate:F1}% exceeds {Threshold}% — alert sent", rate, thresholdPercent);
    }

    private async Task<SystemEmailContent> BuildDigestAsync(IServiceProvider sp, DateTimeOffset newlySuppressedSince, CancellationToken ct)
    {
        var (delivered, bounced, deferred, suppressed) = await CountLast24hAsync(sp, ct);
        var attempts = delivered + bounced + deferred;
        var rate = attempts > 0 ? 100.0 * bounced / attempts : 0;

        var queueDepth = await sp.GetRequiredService<IMessageQueue>().GetQueueDepthAsync(ct);
        var db = sp.GetRequiredService<RelayDbContext>();
        var suppressionCount = await db.SuppressionEntries.IgnoreQueryFilters().CountAsync(ct);

        // Addresses added to the suppression list since the previous report (host-wide). Ordered by Id,
        // which is chronological by construction — avoids an ORDER BY on the DateTimeOffset column.
        var newlySuppressed = await db.SuppressionEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.CreatedUtc >= newlySuppressedSince)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Address, e.Reason, e.CreatedUtc })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("Last 24 hours (all tenants):");
        sb.AppendLine($"  Delivered:  {delivered}");
        sb.AppendLine($"  Bounced:    {bounced}  (bounce rate {rate:F1}%)");
        sb.AppendLine($"  Deferred:   {deferred}");
        sb.AppendLine($"  Suppressed: {suppressed} (skipped — on the suppression list)");
        sb.AppendLine();
        sb.AppendLine($"Queue depth now:       {queueDepth}");
        sb.AppendLine($"Suppression list size: {suppressionCount}");
        sb.AppendLine();

        sb.AppendLine($"Newly suppressed since last report: {newlySuppressed.Count}");
        if (newlySuppressed.Count > 0)
        {
            var shown = newlySuppressed.Take(MaxNewSuppressionsListed).ToList();
            var addrWidth = Math.Min(50, shown.Max(e => e.Address.Length));
            foreach (var e in shown)
                sb.AppendLine($"  {e.Address.PadRight(addrWidth)}  {e.Reason,-10}  {e.CreatedUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
            if (newlySuppressed.Count > shown.Count)
                sb.AppendLine($"  … and {newlySuppressed.Count - shown.Count} more — see the Suppression List page.");
        }
        sb.AppendLine();

        var dnsSettings = await sp.GetRequiredService<IDnsSettingsService>().GetAsync(ct);
        var ips = SplitList(dnsSettings.SendingIpAddresses);
        sb.AppendLine("Sending IP blocklist status:");
        if (ips.Count == 0)
        {
            sb.AppendLine("  (no sending IPs configured)");
        }
        else
        {
            var dns = sp.GetRequiredService<IDnsSetupService>();
            foreach (var ip in ips)
            {
                var r = await dns.CheckBlocklistsAsync(ip, ct);
                var status = r.Status switch
                {
                    DnsRecordStatus.Listed => "LISTED",
                    DnsRecordStatus.Ok => "ok",
                    _ => "unknown"
                };
                sb.AppendLine($"  {ip}: {status}");
            }
        }

        await AppendRejectedSubmissionsSectionAsync(db, sb, ct);
        await AppendHealthSectionAsync(sp, sb, ct);

        return new SystemEmailContent
        {
            Title = "Daily report",
            Paragraphs = [$"Activity summary as of {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, host {Environment.MachineName}."],
            MonospaceBlock = sb.ToString(),
            FooterNote = "Sent by WIN-SMTP-RELAY email reporting — configure under Settings → Reporting.",
        };
    }

    /// <summary>
    /// Appends refused submissions — the mail that never got in. The rest of this digest reports what the
    /// relay did with mail it accepted; without this section a device the relay has been refusing for
    /// months is invisible in the one report the operator actually reads.
    /// <para>
    /// Trusted and untrusted are listed separately and deliberately unequally: a refusal from a configured
    /// network is a device that should be sending and cannot, while refusals from everywhere else are the
    /// relay doing its job against port-25 noise and are reported as a single number.
    /// </para>
    /// </summary>
    private static async Task AppendRejectedSubmissionsSectionAsync(RelayDbContext db, StringBuilder sb, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);

        var recent = await db.RejectedSubmissions.AsNoTracking()
            .Where(r => r.LastSeenUtc >= since)
            .ToListAsync(ct);

        sb.AppendLine();
        sb.AppendLine("Refused submissions (last 24 hours):");

        var untrustedRows = recent.Where(r => !r.IsTrustedSource).ToList();
        var trusted = recent.Where(r => r.IsTrustedSource)
            .OrderByDescending(r => r.Count)
            .ToList();

        sb.AppendLine($"  From untrusted sources: {untrustedRows.Sum(r => r.Count)} attempt(s) "
            + $"from {untrustedRows.Select(r => r.ClientIp).Distinct().Count()} IP(s) — expected; not a problem.");

        if (trusted.Count == 0)
        {
            sb.AppendLine("  From configured networks: none.");
            return;
        }

        sb.AppendLine($"  From configured networks: {trusted.Sum(r => r.Count)} attempt(s) — these are devices that "
            + "cannot send:");

        var shown = trusted.Take(MaxRejectionsListed).ToList();
        foreach (var r in shown)
        {
            var who = r.SenderDomain.Length > 0 ? $"{r.ClientIp} as {r.SenderDomain}" : r.ClientIp;
            var persistent = r.LastSeenUtc - r.FirstSeenUtc > TimeSpan.FromHours(24)
                ? $", failing for {(int)(r.LastSeenUtc - r.FirstSeenUtc).TotalHours}h"
                : "";
            sb.AppendLine($"    {who}: {r.Reason.Describe()} ({r.Count}x{persistent})");
        }
        if (trusted.Count > shown.Count)
            sb.AppendLine($"    … and {trusted.Count - shown.Count} more — see Diagnostics in the admin UI.");
    }

    /// <summary>Appends the latest daily self-check summary (Setup &amp; Health) to the digest body.</summary>
    private static async Task AppendHealthSectionAsync(IServiceProvider sp, StringBuilder sb, CancellationToken ct)
    {
        var snapshot = await sp.GetRequiredService<IHealthCheckSnapshotService>().GetLatestAsync(ct);
        sb.AppendLine();
        if (snapshot is null)
        {
            sb.AppendLine("Setup & health self-check: not run yet.");
            return;
        }

        sb.AppendLine($"Setup & health self-check (as of {snapshot.RunUtc:yyyy-MM-dd HH:mm} UTC):");
        sb.AppendLine($"  Errors:   {snapshot.ErrorCount}");
        sb.AppendLine($"  Warnings: {snapshot.WarningCount}");
        sb.AppendLine($"  OK:       {snapshot.OkCount}");

        var issues = snapshot.Findings
            .Where(x => x.Severity >= HealthSeverity.Warning)
            .OrderByDescending(x => x.Severity)
            .Take(15)
            .ToList();
        if (issues.Count == 0)
        {
            sb.AppendLine("  No problems found.");
            return;
        }

        sb.AppendLine();
        foreach (var x in issues)
        {
            var target = string.IsNullOrWhiteSpace(x.Target) ? "" : $" [{x.Target}]";
            sb.AppendLine($"  [{x.Severity.ToString().ToUpperInvariant()}] {x.Title}{target}");
        }
        if (snapshot.IssueCount > issues.Count)
            sb.AppendLine($"  … and {snapshot.IssueCount - issues.Count} more — see Diagnostics in the admin UI.");
    }

    /// <summary>Delivery-log counts over the last 24h (host-wide). Suppressed skips are excluded from bounces.</summary>
    private static async Task<(int delivered, int bounced, int deferred, int suppressed)> CountLast24hAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<RelayDbContext>();
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var logs = await db.DeliveryLogs.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.TimestampUtc >= since)
            .Select(l => new { l.StatusCode, l.StatusMessage })
            .ToListAsync(ct);

        var suppressed = logs.Count(l => l.StatusMessage != null && l.StatusMessage.StartsWith("Suppressed", StringComparison.OrdinalIgnoreCase));
        var delivered = logs.Count(l => l.StatusCode.StartsWith('2'));
        var deferred = logs.Count(l => l.StatusCode.StartsWith('4'));
        var bounced = logs.Count(l => l.StatusCode.StartsWith('5')
            && !(l.StatusMessage != null && l.StatusMessage.StartsWith("Suppressed", StringComparison.OrdinalIgnoreCase)));
        return (delivered, bounced, deferred, suppressed);
    }

    /// <summary>
    /// Start of the "newly suppressed since the last report" window: the moment the previous daily digest
    /// was sent (its date at the configured send time, UTC). Falls back to the last 24h on the very first
    /// report. Deriving it from the previous send moment — rather than a fixed 24h window — means a skipped
    /// day is still covered: the next report lists everything suppressed since the last one actually went out.
    /// </summary>
    private static DateTimeOffset SuppressionReportCutoff(ReportingSettings settings)
    {
        if (settings.LastDigestSentDate is { } last && TimeOnly.TryParse(settings.DailyTimeUtc, out var t))
            return new DateTimeOffset(last.ToDateTime(t), TimeSpan.Zero);
        return DateTimeOffset.UtcNow.AddHours(-24);
    }

    private bool ShouldAlert(string key, TimeSpan cooldown)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastAlert.TryGetValue(key, out var last) && now - last < cooldown)
            return false;
        _lastAlert[key] = now;
        return true;
    }

    // All system mail (digest + alerts) goes through the single MIME composer so header
    // sanitization and the text+HTML rendering stay identical to the account/verification mail.
    private static Task SendAsync(IMessageQueue queue, string from, string to, string subject, SystemEmailContent content, CancellationToken ct)
        => SystemEmail.EnqueueAsync(queue, from, to, subject, content, TenantDefaults.DefaultTenantId, ct);

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

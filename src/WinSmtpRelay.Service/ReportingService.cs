using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Interfaces;
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
            var body = await BuildDigestAsync(sp, ct);
            await SendAsync(queue, from, to, $"WIN-SMTP-RELAY daily report — {today:yyyy-MM-dd}", body, ct);
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
                $"The sending IP {ip} appears on a DNS blocklist (DNSBL):\r\n\r\n{result.Explanation}\r\n\r\n" +
                "Mail from this IP will be rejected or spam-foldered by many providers. Find and stop the cause " +
                "(spam from a compromised account, misconfiguration), then request delisting at the listing provider. " +
                "Consider relaying outbound mail through a reputable smart host. See the Health page for details.\r\n",
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
            $"The outbound bounce rate over the last 24 hours is {rate:F1}% ({bounced} bounced of {attempts} attempts), " +
            $"above the {thresholdPercent}% alert threshold.\r\n\r\nA high bounce rate harms sending reputation and can " +
            "lead to blocklisting. Check for invalid recipient lists, a misconfigured sender domain (SPF/DKIM), or a " +
            "compromised account. The suppression list already stops repeat delivery to hard-bounced addresses.\r\n",
            ct);
        logger.LogWarning("Reporting: 24h bounce rate {Rate:F1}% exceeds {Threshold}% — alert sent", rate, thresholdPercent);
    }

    private async Task<string> BuildDigestAsync(IServiceProvider sp, CancellationToken ct)
    {
        var (delivered, bounced, deferred, suppressed) = await CountLast24hAsync(sp, ct);
        var attempts = delivered + bounced + deferred;
        var rate = attempts > 0 ? 100.0 * bounced / attempts : 0;

        var queueDepth = await sp.GetRequiredService<IMessageQueue>().GetQueueDepthAsync(ct);
        var db = sp.GetRequiredService<RelayDbContext>();
        var suppressionCount = await db.SuppressionEntries.IgnoreQueryFilters().CountAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"WIN-SMTP-RELAY daily report — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine("Last 24 hours (all tenants):");
        sb.AppendLine($"  Delivered:  {delivered}");
        sb.AppendLine($"  Bounced:    {bounced}  (bounce rate {rate:F1}%)");
        sb.AppendLine($"  Deferred:   {deferred}");
        sb.AppendLine($"  Suppressed: {suppressed} (skipped — on the suppression list)");
        sb.AppendLine();
        sb.AppendLine($"Queue depth now:       {queueDepth}");
        sb.AppendLine($"Suppression list size: {suppressionCount}");
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
        sb.AppendLine();
        sb.AppendLine("— WIN-SMTP-RELAY");
        return sb.ToString();
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

    private bool ShouldAlert(string key, TimeSpan cooldown)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastAlert.TryGetValue(key, out var last) && now - last < cooldown)
            return false;
        _lastAlert[key] = now;
        return true;
    }

    private static async Task SendAsync(IMessageQueue queue, string from, string to, string subject, string body, CancellationToken ct)
    {
        from = Header(from);
        to = Header(to);
        subject = Header(subject);
        var messageId = $"<{Guid.NewGuid():N}@winsmtprelay>";
        var raw = Encoding.UTF8.GetBytes(
            $"From: {from}\r\n" +
            $"To: {to}\r\n" +
            $"Subject: {subject}\r\n" +
            $"Date: {DateTimeOffset.UtcNow:r}\r\n" +
            $"Message-ID: {messageId}\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            body);
        await queue.EnqueueAsync(new QueuedMessage
        {
            MessageId = messageId,
            Sender = from,
            Recipients = to,
            RawMessage = raw,
            SizeBytes = raw.Length,
            TenantId = TenantDefaults.DefaultTenantId,
            NextRetryUtc = DateTimeOffset.UtcNow
        }, ct);
    }

    private static string Header(string value) => value.Replace("\r", "").Replace("\n", "").Trim();

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

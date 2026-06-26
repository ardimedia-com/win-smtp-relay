using System.Net;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// Deliverability / reputation checks per sender domain and sending IP: SPF/DKIM/DMARC and their alignment
/// synthesis, the public hostname, reverse DNS (PTR), DNS blocklists, and outbound-IP drift. Reuses the
/// existing <see cref="IDnsSetupService"/> so the findings match the Deliverability page exactly.
/// </summary>
public sealed class DeliverabilityHealthCheck(
    IDnsSetupService dns,
    IDnsSettingsService dnsSettings,
    IHttpClientFactory httpClientFactory,
    ILogger<DeliverabilityHealthCheck> logger) : HealthCheckBase
{
    public override string Name => "Deliverability";
    protected override string Category => HealthCategories.Deliverability;

    public override async Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct)
    {
        var f = new List<HealthFinding>();
        var settings = await dnsSettings.GetAsync(ct);
        var ips = SplitList(settings.SendingIpAddresses);

        // --- Sender domains: SPF / DKIM / DMARC + the "will it pass DMARC?" synthesis ---
        IReadOnlyList<DomainDnsSetup> domains;
        try { domains = await dns.CheckAllSenderDomainsAsync(ct); }
        catch (Exception ex) { logger.LogWarning(ex, "sender-domain DNS check failed"); domains = []; }

        if (domains.Count == 0)
            f.Add(Info("no-sender-domains", "No sender domains configured",
                "No accepted sender domains are set up, so SPF/DKIM/DMARC can't be verified. Add one under Accepted Domains (Sender)."));

        foreach (var d in domains)
        {
            var a = d.Alignment;
            f.Add(a.Verdict switch
            {
                DmarcAlignmentVerdict.DkimAligned => Ok("dmarc-alignment", $"{d.Domain}: mail will pass DMARC", a.Summary, d.Domain),
                DmarcAlignmentVerdict.SpfConditional => Warn("dmarc-alignment", $"{d.Domain}: DMARC depends on SPF alignment only", a.Summary, d.Domain,
                    "Configure a DKIM key for this domain so alignment is robust regardless of the envelope-from."),
                _ => Err("dmarc-alignment", $"{d.Domain}: mail will fail DMARC", a.Summary, d.Domain,
                    "Publish SPF and set up a DKIM key for this domain."),
            });

            // SPF/DKIM/DMARC blast radius differs: a missing SPF or a drifted DKIM key is the dangerous one.
            f.Add(MapRecord("spf", $"{d.Domain}: SPF record", d.Domain, d.Spf, missing: HealthSeverity.Error, mismatch: HealthSeverity.Warning));
            f.Add(MapRecord("dkim", $"{d.Domain}: DKIM record", d.Domain, d.Dkim, missing: HealthSeverity.Warning, mismatch: HealthSeverity.Error));
            f.Add(MapRecord("dmarc", $"{d.Domain}: DMARC record", d.Domain, d.Dmarc, missing: HealthSeverity.Warning, mismatch: HealthSeverity.Warning));
        }

        // --- Public hostname (A/AAAA → sending IP, used in SPF a: and as the EHLO name) ---
        if (string.IsNullOrWhiteSpace(settings.PublicHostname))
            f.Add(Info("no-public-hostname", "No public hostname configured",
                "Set the relay's public hostname under Settings → Sending identity — it's used in SPF (a:) and as the EHLO name."));
        else
        {
            var host = await dns.CheckHostnameAsync(ct);
            f.Add(MapRecord("hostname", $"Public hostname {settings.PublicHostname}", settings.PublicHostname!, host,
                missing: HealthSeverity.Warning, mismatch: HealthSeverity.Warning));
        }

        // --- Per sending IP: reverse DNS (PTR) + DNS blocklist ---
        if (ips.Count == 0)
            f.Add(Info("no-sending-ips", "No sending IPs configured",
                "Add the relay's public sending IP(s) under Settings → Sending identity so reverse DNS and blocklist status can be checked."));

        foreach (var ip in ips)
        {
            var ptr = await dns.CheckReverseDnsAsync(ip, ct);
            f.Add(MapRecord("reverse-dns", $"Reverse DNS for {ip}", ip, ptr, missing: HealthSeverity.Error, mismatch: HealthSeverity.Warning));

            var bl = await dns.CheckBlocklistsAsync(ip, ct);
            f.Add(bl.Status switch
            {
                DnsRecordStatus.Listed => Err("blocklist", $"Sending IP {ip} is blocklisted", bl.Explanation, ip,
                    "Find and stop the cause, request delisting, or send via a clean smart host."),
                DnsRecordStatus.Ok => Ok("blocklist", $"Sending IP {ip} is not blocklisted", bl.Explanation, ip),
                _ => Info("blocklist", $"Blocklist status for {ip} is unknown", bl.Explanation, ip),
            });
        }

        // --- Egress IP drift: this server's actual public IP vs the configured sending IPs ---
        var detected = await DetectPublicIpAsync(ct);
        if (detected is not null && ips.Count > 0)
            f.Add(ips.Any(s => IpEquals(s, detected))
                ? Ok("egress-ip", "Outbound IP matches configuration", $"This server sends from {detected}, one of your configured sending IPs.", detected)
                : Err("egress-ip-drift", "Outbound IP is not a configured sending IP",
                    $"This server's detected public IP {detected} is not among your configured sending IPs ({string.Join(", ", ips)}). " +
                    "If the IP changed (e.g. a dynamic ISP address), your SPF / reverse-DNS / sending-IP settings now point at the wrong address.",
                    detected, "Update the sending IPs under Settings → Sending identity (and the SPF / PTR records)."));
        else if (detected is not null)
            f.Add(Info("egress-ip", "Outbound IP detected", $"This server sends from {detected}; no sending IPs are configured to compare against.", detected));
        else
            f.Add(Info("egress-ip", "Could not detect outbound IP", "Best-effort public-IP lookup failed (no internet, or blocked)."));

        return f;
    }

    private HealthFinding MapRecord(string code, string title, string target, DnsRecordResult rec,
        HealthSeverity missing, HealthSeverity mismatch)
    {
        var hint = rec.Suggested ?? (string.IsNullOrWhiteSpace(rec.Expected) ? null : $"Publish: {rec.Expected}");
        return rec.Status switch
        {
            DnsRecordStatus.Ok => Ok(code, title, rec.Explanation, target),
            DnsRecordStatus.Listed => Err(code, title, rec.Explanation, target),
            DnsRecordStatus.Missing => new HealthFinding(Category, code, missing, title, rec.Explanation, target, hint),
            DnsRecordStatus.Mismatch => new HealthFinding(Category, code, mismatch, title, rec.Explanation, target, hint),
            _ => Info(code, title, rec.Explanation, target), // NotConfigured — nothing expected yet
        };
    }

    private async Task<string?> DetectPublicIpAsync(CancellationToken ct)
    {
        string[] endpoints = ["https://api.ipify.org", "https://checkip.amazonaws.com", "https://ifconfig.me/ip"];
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        foreach (var url in endpoints)
        {
            try
            {
                var text = (await client.GetStringAsync(url, ct)).Trim();
                if (IPAddress.TryParse(text, out var ip))
                    return ip.ToString();
            }
            catch { /* try the next endpoint */ }
        }
        return null;
    }

    private static bool IpEquals(string? a, string? b) =>
        IPAddress.TryParse(a?.Trim(), out var ia) && IPAddress.TryParse(b?.Trim(), out var ib) && ia.Equals(ib);

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? []
        : [.. value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Configuration;
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
    ITenantService tenants,
    ISendConnectorService connectors,
    IDomainRouteService routes,
    IOptions<DeliveryOptions> deliveryOptions,
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

        // When every message leaves through a smart host, receivers see the smart host — its IP, its EHLO name
        // and its DKIM signature — not this server. The direct-delivery findings then describe nothing real.
        OutboundRouting? routing = null;
        try { routing = await OutboundRoutingLoader.LoadAsync(tenants, connectors, routes, deliveryOptions.Value, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "evaluating the outbound routing failed"); }
        var smartHostOnly = routing is { SmartHostOnly: true };

        // --- Sender domains: SPF / DKIM / DMARC + the "will it pass DMARC?" synthesis ---
        IReadOnlyList<DomainDnsSetup> domains;
        try { domains = await dns.CheckAllSenderDomainsAsync(ct); }
        catch (Exception ex) { logger.LogWarning(ex, "sender-domain DNS check failed"); domains = []; }

        if (domains.Count == 0)
            f.Add(Info("no-sender-domains", "No sender domains configured",
                "No accepted sender domains are set up, so SPF/DKIM/DMARC can't be verified. Add one under Accepted Domains (Sender)."));

        foreach (var d in domains)
        {
            // The alignment already accounts for a smart host: its DKIM signature decides when all mail leaves through one.
            var a = d.Alignment;
            f.Add(a.Verdict switch
            {
                DmarcAlignmentVerdict.DkimAligned => Ok("dmarc-alignment",
                    d.SmartHost is null ? $"{d.Domain}: mail will pass DMARC" : $"{d.Domain}: mail will pass DMARC via the smart host",
                    a.Summary, d.Domain),
                DmarcAlignmentVerdict.SpfConditional => Warn("dmarc-alignment", $"{d.Domain}: DMARC depends on SPF alignment only", a.Summary, d.Domain,
                    "Configure a DKIM key for this domain so alignment is robust regardless of the envelope-from."),
                DmarcAlignmentVerdict.SmartHostDependent => Info("dmarc-alignment", $"{d.Domain}: DMARC depends on the smart host", a.Summary, d.Domain,
                    "Check the domain authentication (DKIM) at the smart host."),
                _ => Err("dmarc-alignment", $"{d.Domain}: mail will fail DMARC", a.Summary, d.Domain,
                    d.SmartHost is null
                        ? "Publish SPF and set up a DKIM key for this domain."
                        : "Authenticate the domain at the smart host's provider and publish the DKIM records it gives you."),
            });

            // SPF/DKIM/DMARC blast radius differs: a missing SPF or a drifted DKIM key is the dangerous one.
            // The relay's SPF expectation is built from this server's sending IPs, which receivers never see when
            // everything leaves through a smart host (the smart host's own return-path domain carries SPF then).
            f.Add(smartHostOnly && d.Spf.Status is DnsRecordStatus.Missing or DnsRecordStatus.Mismatch
                ? Info("spf", $"{d.Domain}: SPF record", $"Not relevant for delivery: every message leaves through a smart host "
                    + $"({routing!.SmartHostList}), whose own sending domain is what SPF checks.", d.Domain)
                : MapRecord("spf", $"{d.Domain}: SPF record", d.Domain, d.Spf, missing: HealthSeverity.Error, mismatch: HealthSeverity.Warning));
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

        // --- EHLO name: what outbound delivery will actually announce ---
        // PTR/FCrDNS alignment against the public hostname is covered by the reverse-dns finding below;
        // this finding covers the announced name itself (paused / implicit fallback / explicit).
        var ehlo = EhloHostname.Resolve(null, settings.PublicHostname);
        if (ehlo is null)
            f.Add(Err("ehlo", "No EHLO hostname — outbound delivery is paused",
                "No fully qualified EHLO name is available: no public hostname is configured and this machine has no DNS suffix. "
                + "Outbound delivery refuses to run rather than announce an unqualified name, which strict receivers reject "
                + "(\"550 Is neither a FQDN nor a IP literal\"). Queued mail is held until this is fixed.",
                hint: "Set the public hostname under Settings → Sending identity to the FQDN matching the outbound IP's PTR record."));
        else if (string.IsNullOrWhiteSpace(settings.PublicHostname) && smartHostOnly)
            f.Add(Info("ehlo", $"EHLO announces the machine FQDN {ehlo}",
                $"No public hostname is configured, so this server greets its smart host ({routing!.SmartHostList}) with its "
                + "machine FQDN. The smart host authenticates the connection; recipients never see this name.", ehlo));
        else if (string.IsNullOrWhiteSpace(settings.PublicHostname))
            f.Add(Warn("ehlo", $"EHLO falls back to the machine FQDN {ehlo}",
                "No public hostname is configured, so outbound delivery announces this machine's FQDN. That works only while "
                + "it matches the outbound IP's PTR record — receivers score the EHLO/PTR/SPF constellation.",
                ehlo, "Set the public hostname under Settings → Sending identity explicitly (the FQDN matching the PTR)."));
        else
            f.Add(Ok("ehlo", $"Outbound EHLO announces {ehlo}",
                "Send connectors without an explicit EHLO override announce this name. PTR alignment is checked below.", ehlo));

        // --- Per sending IP: reverse DNS (PTR) + DNS blocklist ---
        if (ips.Count == 0)
            f.Add(Info("no-sending-ips", "No sending IPs configured",
                "Add the relay's public sending IP(s) under Settings → Sending identity so reverse DNS and blocklist status can be checked."));

        foreach (var ip in ips)
        {
            // A private address never reaches a receiver: SPF, reverse DNS and blocklists are all about the public
            // address behind the NAT, so checking the private one only produces misleading findings.
            if (IpAddressScope.TryParseAddressOrNetwork(ip, out var parsedIp) && IpAddressScope.IsPrivateOrLoopback(parsedIp))
            {
                f.Add(Err("sending-ip-private", $"Sending IP {ip} is a private address",
                    $"{ip} is a private address that receiving mail servers never see, so the SPF record recommended from it, "
                    + "the reverse-DNS and the blocklist checks all point at the wrong address. Behind NAT, the sending IP is "
                    + "the router's public address.",
                    ip, "Replace it under Settings → Sending identity with the public address (\"Detect public IP\" proposes it). "
                    + "The local source address a tenant binds to stays under Tenants → Source IP."));
                continue;
            }

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
                : smartHostOnly
                ? Info("egress-ip-drift", "Outbound IP is not a configured sending IP",
                    $"This server's detected public IP {detected} is not among the configured sending IPs ({string.Join(", ", ips)}). "
                    + $"Receivers do not see it, because every message leaves through a smart host ({routing!.SmartHostList}).", detected)
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

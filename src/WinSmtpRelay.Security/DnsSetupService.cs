using System.Security.Cryptography;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Security;

public class DnsSetupService(
    ILookupClient dns,
    IOptions<DnsOptions> options,
    IDkimDomainService dkimDomains,
    IAcceptedSenderDomainService senderDomains,
    IDnsSettingsService dnsSettings,
    ILogger<DnsSetupService> logger) : IDnsSetupService
{
    private readonly DnsOptions _options = options.Value;

    // The runtime (DB) recommendation inputs, loaded once per scope. Falls back to the appsettings
    // values until loaded (e.g. when only the synchronous Build* methods are exercised).
    private DnsOptions? _effective;
    private DnsOptions Current => _effective ?? _options;

    private async Task EnsureEffectiveAsync(CancellationToken ct)
    {
        if (_effective is not null)
            return;

        var s = await dnsSettings.GetAsync(ct);
        _effective = new DnsOptions
        {
            PublicHostname = s.PublicHostname,
            SendingIpAddresses = SplitList(s.SendingIpAddresses),
            SpfIncludes = SplitList(s.SpfIncludes),
            SpfAllQualifier = s.SpfAllQualifier,
            DmarcReportEmail = s.DmarcReportEmail,
            DmarcPolicy = s.DmarcPolicy,
            DmarcPercentage = s.DmarcPercentage,
        };
    }

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private bool HasSpfIdentity =>
        Current.SendingIpAddresses.Count > 0
        || !string.IsNullOrWhiteSpace(Current.PublicHostname)
        || Current.SpfIncludes.Count > 0;

    public string BuildRecommendedSpf()
    {
        var current = Current;
        var parts = new List<string> { "v=spf1" };
        foreach (var ip in current.SendingIpAddresses)
            parts.Add((ip.Contains(':') ? "ip6:" : "ip4:") + ip.Trim());
        if (!string.IsNullOrWhiteSpace(current.PublicHostname))
            parts.Add("a:" + current.PublicHostname.Trim());
        foreach (var include in current.SpfIncludes)
            parts.Add("include:" + include.Trim());
        parts.Add(string.IsNullOrWhiteSpace(current.SpfAllQualifier) ? "~all" : current.SpfAllQualifier.Trim());
        return string.Join(' ', parts);
    }

    public string BuildRecommendedDmarc(string domain)
    {
        var current = Current;
        var policy = string.IsNullOrWhiteSpace(current.DmarcPolicy) ? "none" : current.DmarcPolicy.Trim();
        var record = $"v=DMARC1; p={policy}";
        if (!string.IsNullOrWhiteSpace(current.DmarcReportEmail))
            record += $"; rua=mailto:{current.DmarcReportEmail.Trim()}";
        record += $"; pct={current.DmarcPercentage}";
        return record;
    }

    public async Task<DomainDnsSetup> CheckDomainAsync(string domain, CancellationToken ct = default)
    {
        await EnsureEffectiveAsync(ct);
        var dkimRows = await dkimDomains.GetAllAsync(ct);
        return await CheckCoreAsync(domain, dkimRows, ct);
    }

    public async Task<IReadOnlyList<DomainDnsSetup>> CheckAllSenderDomainsAsync(CancellationToken ct = default)
    {
        await EnsureEffectiveAsync(ct);

        // Load DB-backed config once; DNS lookups below touch no DbContext, so they are safe to
        // run sequentially over a shared scoped DbContext (no concurrent EF access).
        var senders = await senderDomains.GetAllAsync(ct);
        var dkimRows = await dkimDomains.GetAllAsync(ct);

        var results = new List<DomainDnsSetup>(senders.Count);
        foreach (var sender in senders)
            results.Add(await CheckCoreAsync(sender.Domain, dkimRows, ct));
        return results;
    }

    private async Task<DomainDnsSetup> CheckCoreAsync(string domain, IReadOnlyList<DkimDomain> dkimRows, CancellationToken ct)
    {
        domain = domain.Trim().ToLowerInvariant();
        return new DomainDnsSetup(
            domain,
            await CheckSpfAsync(domain, ct),
            await CheckDkimAsync(domain, dkimRows, ct),
            await CheckDmarcAsync(domain, ct));
    }

    private async Task<DnsRecordResult> CheckSpfAsync(string domain, CancellationToken ct)
    {
        var live = await ResolveTxtAsync(domain, t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase), ct);

        if (!HasSpfIdentity)
            return new DnsRecordResult("SPF", domain, "", live, DnsRecordStatus.NotConfigured,
                "Configure the relay's sending identity (Dns:SendingIpAddresses / Dns:PublicHostname) to get a recommended SPF record.");

        var expected = BuildRecommendedSpf();
        if (live is null)
            return new DnsRecordResult("SPF", domain, expected, null, DnsRecordStatus.Missing, "No SPF record is published.");

        var covers = SpfCovers(live, expected);
        return new DnsRecordResult("SPF", domain, expected, live,
            covers ? DnsRecordStatus.Ok : DnsRecordStatus.Mismatch,
            covers ? "The published SPF record authorises this relay." : "The published SPF record does not authorise this relay's senders.");
    }

    private async Task<DnsRecordResult> CheckDmarcAsync(string domain, CancellationToken ct)
    {
        var name = $"_dmarc.{domain}";
        var live = await ResolveTxtAsync(name, t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase), ct);
        var expected = BuildRecommendedDmarc(domain);

        return live is null
            ? new DnsRecordResult("DMARC", name, expected, null, DnsRecordStatus.Missing, "No DMARC record is published.")
            : new DnsRecordResult("DMARC", name, expected, live, DnsRecordStatus.Ok, "A DMARC record is published.");
    }

    private async Task<DnsRecordResult> CheckDkimAsync(string domain, IReadOnlyList<DkimDomain> dkimRows, CancellationToken ct)
    {
        var dkim = dkimRows.FirstOrDefault(d => string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase));
        if (dkim is null)
            return new DnsRecordResult("DKIM", $"<selector>._domainkey.{domain}", "", null, DnsRecordStatus.NotConfigured,
                "No DKIM key is configured for this domain — set one up on the DKIM page.");

        var name = $"{dkim.Selector}._domainkey.{domain}";
        var expected = TryDeriveDkimTxt(dkim.PrivateKeyPath);
        if (expected is null)
            return new DnsRecordResult("DKIM", name, "", null, DnsRecordStatus.NotConfigured,
                "The DKIM private key file is missing or unreadable.");

        var live = await ResolveTxtAsync(name, t => t.Contains("p=", StringComparison.OrdinalIgnoreCase), ct);
        if (live is null)
            return new DnsRecordResult("DKIM", name, expected, null, DnsRecordStatus.Missing, "No DKIM record is published.");

        var match = string.Equals(ExtractDkimKey(expected), ExtractDkimKey(live), StringComparison.Ordinal);
        return new DnsRecordResult("DKIM", name, expected, live,
            match ? DnsRecordStatus.Ok : DnsRecordStatus.Mismatch,
            match ? "The published DKIM public key matches." : "The published DKIM key differs from the configured key.");
    }

    public async Task<DnsRecordResult> CheckHostnameAsync(CancellationToken ct = default)
    {
        await EnsureEffectiveAsync(ct);
        var host = Current.PublicHostname?.Trim();
        if (string.IsNullOrWhiteSpace(host))
            return new DnsRecordResult("A / AAAA", "(public hostname)", "", null, DnsRecordStatus.NotConfigured,
                "Set the relay's public hostname in Settings — it is used in SPF (a:) and as the EHLO name, and should resolve to your sending IP.");

        var ips = await ResolveAddressesAsync(host, ct);
        var expected = Current.SendingIpAddresses.Count > 0 ? string.Join(", ", Current.SendingIpAddresses) : "your sending IP";
        if (ips.Count == 0)
            return new DnsRecordResult("A / AAAA", host, expected, null, DnsRecordStatus.Missing,
                "The public hostname does not resolve. Publish an A/AAAA record pointing it at your sending IP.");

        // IP comparison via IPAddress so different IPv6 textual forms still match.
        var ok = Current.SendingIpAddresses.Count == 0 || ips.Any(ip => IpInList(Current.SendingIpAddresses, ip));
        return new DnsRecordResult("A / AAAA", host, expected, string.Join(", ", ips),
            ok ? DnsRecordStatus.Ok : DnsRecordStatus.Mismatch,
            ok ? "The public hostname resolves." : "The public hostname resolves to an IP that is not among your configured sending IPs.");
    }

    public async Task<DnsRecordResult> CheckMxAsync(string domain, CancellationToken ct = default)
    {
        await EnsureEffectiveAsync(ct);
        domain = domain.Trim().ToLowerInvariant();
        var host = Current.PublicHostname?.Trim().ToLowerInvariant();
        var mx = await ResolveMxAsync(domain, ct);
        var live = mx.Count > 0 ? string.Join(", ", mx) : null;
        var expected = string.IsNullOrWhiteSpace(host) ? "this relay" : host;

        if (mx.Count == 0)
            return new DnsRecordResult("MX", domain, expected, null, DnsRecordStatus.Missing,
                "No MX record found — this relay will not receive mail for this domain. Publish an MX record pointing to it.");
        if (string.IsNullOrWhiteSpace(host) && Current.SendingIpAddresses.Count == 0)
            return new DnsRecordResult("MX", domain, expected, live, DnsRecordStatus.NotConfigured,
                "Set the relay's public hostname (or sending IPs) in Settings so the MX target can be verified.");

        // Points here if an MX host matches our hostname, or resolves to one of our sending IPs.
        var pointsHere = !string.IsNullOrWhiteSpace(host) && mx.Any(m => string.Equals(m, host, StringComparison.OrdinalIgnoreCase));
        if (!pointsHere && Current.SendingIpAddresses.Count > 0)
        {
            foreach (var m in mx)
            {
                var mxIps = await ResolveAddressesAsync(m, ct);
                if (mxIps.Any(ip => IpInList(Current.SendingIpAddresses, ip))) { pointsHere = true; break; }
            }
        }
        return new DnsRecordResult("MX", domain, expected, live,
            pointsHere ? DnsRecordStatus.Ok : DnsRecordStatus.Mismatch,
            pointsHere ? "An MX record points to this relay." : "MX records exist but none resolve to this relay (by hostname or sending IP).");
    }

    public async Task<DnsRecordResult> CheckReverseDnsAsync(string ipAddress, CancellationToken ct = default)
    {
        await EnsureEffectiveAsync(ct);
        ipAddress = ipAddress.Trim();
        if (!System.Net.IPAddress.TryParse(ipAddress, out _))
            return new DnsRecordResult("PTR (reverse DNS)", ipAddress, "", null, DnsRecordStatus.NotConfigured,
                "Not a valid IP address — fix the sending IPs (Settings) or the tenant's egress IP.");

        var host = Current.PublicHostname?.Trim().ToLowerInvariant();
        var ptr = await ResolvePtrAsync(ipAddress, ct);
        var expected = string.IsNullOrWhiteSpace(host) ? "an FQDN that resolves back to this IP" : host;

        if (ptr is null)
            return new DnsRecordResult("PTR (reverse DNS)", ipAddress, expected, null, DnsRecordStatus.Missing,
                "No reverse DNS for this IP. Many receivers distrust or reject mail from IPs without a PTR — ask your IP/hosting provider to set one matching your hostname.");

        // FCrDNS: the PTR hostname must forward-resolve back to this IP.
        var forwardIps = await ResolveAddressesAsync(ptr, ct);
        var fcrdns = forwardIps.Any(ip => IpEquals(ip, ipAddress));

        if (string.IsNullOrWhiteSpace(host))
            return new DnsRecordResult("PTR (reverse DNS)", ipAddress, expected, ptr,
                fcrdns ? DnsRecordStatus.Ok : DnsRecordStatus.Mismatch,
                fcrdns
                    ? "Reverse DNS forward-confirms (FCrDNS). Set a public hostname to also check alignment."
                    : "Reverse DNS is published but its hostname does not resolve back to this IP (FCrDNS fails).");

        var matchesHost = string.Equals(ptr, host, StringComparison.OrdinalIgnoreCase);
        if (matchesHost && fcrdns)
            return new DnsRecordResult("PTR (reverse DNS)", ipAddress, expected, ptr, DnsRecordStatus.Ok,
                "Reverse DNS matches the public hostname and forward-confirms (FCrDNS).");

        var why = !matchesHost
            ? $"Reverse DNS is '{ptr}', not your public hostname; receivers may distrust this IP."
            : "Reverse DNS matches the hostname, but the hostname does not resolve back to this IP (FCrDNS incomplete).";
        return new DnsRecordResult("PTR (reverse DNS)", ipAddress, expected, ptr, DnsRecordStatus.Mismatch, why);
    }

    private static bool IpEquals(string a, string b) =>
        System.Net.IPAddress.TryParse(a, out var ia) && System.Net.IPAddress.TryParse(b, out var ib) && ia.Equals(ib);

    private static bool IpInList(IEnumerable<string> list, string ip) =>
        System.Net.IPAddress.TryParse(ip, out var target)
        && list.Any(s => System.Net.IPAddress.TryParse(s.Trim(), out var a) && a.Equals(target));

    private async Task<List<string>> ResolveAddressesAsync(string host, CancellationToken ct)
    {
        try
        {
            var a = await dns.QueryAsync(host, QueryType.A, cancellationToken: ct);
            var aaaa = await dns.QueryAsync(host, QueryType.AAAA, cancellationToken: ct);
            // Dedupe by IPAddress (handles differing IPv6 textual forms), then render canonical strings.
            return a.Answers.OfType<ARecord>().Select(r => r.Address)
                .Concat(aaaa.Answers.OfType<AaaaRecord>().Select(r => r.Address))
                .Distinct()
                .Select(ip => ip.ToString())
                .ToList();
        }
        catch (DnsResponseException) { return []; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DNS A/AAAA lookup failed for {Host}", host);
            return [];
        }
    }

    // Returns MX exchange hostnames (lowercased, trailing dot stripped), ordered by preference.
    private async Task<List<string>> ResolveMxAsync(string domain, CancellationToken ct)
    {
        try
        {
            var result = await dns.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
            return result.Answers.OfType<MxRecord>()
                .OrderBy(m => m.Preference)
                .Select(m => m.Exchange.Value.TrimEnd('.').ToLowerInvariant())
                .ToList();
        }
        catch (DnsResponseException) { return []; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DNS MX lookup failed for {Domain}", domain);
            return [];
        }
    }

    private async Task<string?> ResolvePtrAsync(string ipAddress, CancellationToken ct)
    {
        if (!System.Net.IPAddress.TryParse(ipAddress, out var addr))
            return null;
        try
        {
            var result = await dns.QueryReverseAsync(addr, ct);
            return result.Answers.OfType<PtrRecord>()
                .Select(p => p.PtrDomainName.Value.TrimEnd('.').ToLowerInvariant())
                .FirstOrDefault();
        }
        catch (DnsResponseException) { return null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reverse DNS lookup failed for {Ip}", ipAddress);
            return null;
        }
    }

    private const string OwnershipPrefix = "winsmtprelay-verification=";

    public string BuildOwnershipRecord(string token) => OwnershipPrefix + token;

    public async Task<bool> CheckOwnershipAsync(string domain, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var expected = BuildOwnershipRecord(token);
        var found = await ResolveTxtAsync(domain,
            txt => txt.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase), ct);
        return found is not null;
    }

    private async Task<string?> ResolveTxtAsync(string name, Func<string, bool> predicate, CancellationToken ct)
    {
        try
        {
            var result = await dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
            return result.Answers
                .OfType<TxtRecord>()
                .Select(txt => string.Join("", txt.Text)) // long records arrive as multiple strings
                .FirstOrDefault(predicate);
        }
        catch (DnsResponseException)
        {
            return null; // NXDOMAIN / no record
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DNS TXT lookup failed for {Name}", name);
            return null;
        }
    }

    /// <summary>Derives "v=DKIM1; k=rsa; p=&lt;SubjectPublicKeyInfo base64&gt;" from a stored private key. Returns null if the file is missing/unreadable.</summary>
    private string? TryDeriveDkimTxt(string privateKeyPath)
    {
        try
        {
            if (!File.Exists(privateKeyPath))
                return null;
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(privateKeyPath)); // handles PKCS#1 and PKCS#8
            return "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to derive DKIM public key from {Path}", privateKeyPath);
            return null;
        }
    }

    // SPF is satisfied if the published record contains every recommended mechanism (ignoring the "all" qualifier and order).
    private static bool SpfCovers(string live, string expected)
    {
        var liveTokens = Tokenize(live);
        return Tokenize(expected)
            .Where(t => !t.Equals("v=spf1", StringComparison.OrdinalIgnoreCase) && !t.EndsWith("all", StringComparison.OrdinalIgnoreCase))
            .All(liveTokens.Contains);

        static HashSet<string> Tokenize(string record) =>
            new(record.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
    }

    // Compares the whitespace-stripped base64 after p= (providers split long TXT values across quoted chunks).
    private static string ExtractDkimKey(string record)
    {
        var idx = record.IndexOf("p=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return "";
        var value = record[(idx + 2)..];
        var semicolon = value.IndexOf(';');
        if (semicolon >= 0)
            value = value[..semicolon];
        return new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }
}

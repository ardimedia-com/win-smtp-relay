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
    ILogger<DnsSetupService> logger) : IDnsSetupService
{
    private readonly DnsOptions _options = options.Value;

    private bool HasSpfIdentity =>
        _options.SendingIpAddresses.Count > 0
        || !string.IsNullOrWhiteSpace(_options.PublicHostname)
        || _options.SpfIncludes.Count > 0;

    public string BuildRecommendedSpf()
    {
        var parts = new List<string> { "v=spf1" };
        foreach (var ip in _options.SendingIpAddresses)
            parts.Add((ip.Contains(':') ? "ip6:" : "ip4:") + ip.Trim());
        if (!string.IsNullOrWhiteSpace(_options.PublicHostname))
            parts.Add("a:" + _options.PublicHostname.Trim());
        foreach (var include in _options.SpfIncludes)
            parts.Add("include:" + include.Trim());
        parts.Add(string.IsNullOrWhiteSpace(_options.SpfAllQualifier) ? "~all" : _options.SpfAllQualifier.Trim());
        return string.Join(' ', parts);
    }

    public string BuildRecommendedDmarc(string domain)
    {
        var policy = string.IsNullOrWhiteSpace(_options.DmarcPolicy) ? "none" : _options.DmarcPolicy.Trim();
        var record = $"v=DMARC1; p={policy}";
        if (!string.IsNullOrWhiteSpace(_options.DmarcReportEmail))
            record += $"; rua=mailto:{_options.DmarcReportEmail.Trim()}";
        record += $"; pct={_options.DmarcPercentage}";
        return record;
    }

    public async Task<DomainDnsSetup> CheckDomainAsync(string domain, CancellationToken ct = default)
    {
        var dkimRows = await dkimDomains.GetAllAsync(ct);
        return await CheckCoreAsync(domain, dkimRows, ct);
    }

    public async Task<IReadOnlyList<DomainDnsSetup>> CheckAllSenderDomainsAsync(CancellationToken ct = default)
    {
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

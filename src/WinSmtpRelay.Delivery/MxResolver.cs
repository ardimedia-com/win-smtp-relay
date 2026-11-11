using System.Net;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Delivery;

public class MxResolver(ILookupClient dns, ILogger<MxResolver> logger) : IMxResolver
{
    public async Task<IReadOnlyList<string>> ResolveMxAsync(string domain, CancellationToken cancellationToken = default)
    {
        var response = await QueryAsync(domain, QueryType.MX, cancellationToken);
        ThrowOnError(domain, response);

        var mx = response.Answers.OfType<MxRecord>().ToList();

        // RFC 7505 null MX: a single MX with an empty exchange («.») — the domain explicitly accepts no mail.
        if (mx.Any(r => IsRoot(r.Exchange.Value)))
            throw new MxLookupException($"Domain {domain} does not accept mail (null MX, RFC 7505)", isPermanent: true);

        var hosts = mx
            .GroupBy(r => r.Preference)
            .OrderBy(g => g.Key)
            // Equal preference: random order, so load spreads across a domain's equivalent mail hosts.
            .SelectMany(g => g.OrderBy(_ => Random.Shared.Next()))
            .Select(r => r.Exchange.Value.TrimEnd('.'))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToList();

        if (hosts.Count > 0)
        {
            logger.LogDebug("MX for {Domain}: {MxRecords}", domain, string.Join(", ", hosts));
            return hosts;
        }

        // RFC 5321 §5.1: no MX → the domain itself is the mail host (implicit MX) — but only if it has an address.
        // A domain with neither cannot receive mail; retrying for two days changes nothing.
        var addresses = await ResolveHostAddressesAsync(domain, cancellationToken);
        if (addresses.Count == 0)
            throw new MxLookupException($"Domain {domain} has no MX and no address record", isPermanent: true);

        logger.LogDebug("No MX for {Domain}, falling back to its address record", domain);
        return [domain];
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken = default)
    {
        var literal = host.Trim('[', ']');
        if (IPAddress.TryParse(literal, out var ip))
            return [ip];

        var a = await QueryAsync(host, QueryType.A, cancellationToken);
        ThrowOnError(host, a);
        var aaaa = await QueryAsync(host, QueryType.AAAA, cancellationToken);
        ThrowOnError(host, aaaa);

        return
        [
            .. a.Answers.OfType<ARecord>().Select(r => r.Address),
            .. aaaa.Answers.OfType<AaaaRecord>().Select(r => r.Address),
        ];
    }

    private async Task<IDnsQueryResponse> QueryAsync(string name, QueryType type, CancellationToken ct)
    {
        try
        {
            return await dns.QueryAsync(name, type, cancellationToken: ct);
        }
        catch (DnsResponseException ex)
        {
            // Timeouts and unreachable resolvers: a DNS outage, not a verdict on the domain.
            logger.LogWarning(ex, "DNS {Type} lookup failed for {Name}", type, name);
            throw new MxLookupException($"DNS lookup ({type}) for {name} failed: {ex.Message}", isPermanent: false, ex);
        }
    }

    private static void ThrowOnError(string name, IDnsQueryResponse response)
    {
        if (!response.HasError)
            return;

        // NXDOMAIN is authoritative: the domain does not exist. Everything else (SERVFAIL, REFUSED, ...) may pass.
        if (response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
            throw new MxLookupException($"Domain {name} does not exist (NXDOMAIN)", isPermanent: true);
        throw new MxLookupException(
            $"DNS lookup for {name} failed: {response.Header.ResponseCode} {response.ErrorMessage}".TrimEnd(), isPermanent: false);
    }

    private static bool IsRoot(string exchange) => exchange is "" or ".";
}

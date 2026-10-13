using System.Net;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Security.Models;

namespace WinSmtpRelay.Security;

/// <summary>
/// Inbound SPF check (RFC 7208 check_host) for a received message's MAIL FROM domain. The evaluation itself is
/// <see cref="SpfEvaluator"/> — the same code the deliverability check uses for outbound — resolved through the
/// host's DNS resolver; this class adds the verdict mapping and turns DNS failures into TempError.
/// </summary>
public class SpfValidator
{
    private readonly ILookupClient _dns;
    private readonly ILogger<SpfValidator> _logger;

    public SpfValidator(ILookupClient dns, ILogger<SpfValidator> logger)
    {
        _dns = dns;
        _logger = logger;
    }

    public async Task<SpfCheckResult> CheckAsync(
        IPAddress senderIp,
        string mailFromDomain,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mailFromDomain))
            return new SpfCheckResult(SpfVerdict.None, "no domain");

        try
        {
            var result = await SpfEvaluator.EvaluateAsync(mailFromDomain, senderIp, new Resolver(_dns), cancellationToken);
            return result switch
            {
                SpfResult.Pass => new SpfCheckResult(SpfVerdict.Pass, $"{mailFromDomain} authorises {senderIp}"),
                SpfResult.Fail => new SpfCheckResult(SpfVerdict.Fail, $"{mailFromDomain} does not authorise {senderIp} (-all)"),
                SpfResult.SoftFail => new SpfCheckResult(SpfVerdict.SoftFail, $"{mailFromDomain} does not authorise {senderIp} (~all)"),
                SpfResult.PermError => new SpfCheckResult(SpfVerdict.PermError, $"SPF record of {mailFromDomain} cannot be evaluated"),
                SpfResult.None => new SpfCheckResult(SpfVerdict.None, $"no SPF record for {mailFromDomain}"),
                _ => new SpfCheckResult(SpfVerdict.Neutral, "no mechanism matched"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SPF check error for {Domain}", mailFromDomain);
            return new SpfCheckResult(SpfVerdict.TempError, $"error: {ex.Message}");
        }
    }

    private sealed class Resolver(ILookupClient dns) : ISpfDnsResolver
    {
        public async Task<string?> GetSpfRecordAsync(string domain, CancellationToken ct)
        {
            try
            {
                var result = await dns.QueryAsync(domain, QueryType.TXT, cancellationToken: ct);
                return result.Answers
                    .OfType<TxtRecord>()
                    .Select(txt => string.Join("", txt.Text))
                    .FirstOrDefault(t => t.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase) ||
                                         t.Equals("v=spf1", StringComparison.OrdinalIgnoreCase));
            }
            catch (DnsResponseException)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<IPAddress>> GetAddressesAsync(string host, CancellationToken ct)
        {
            var list = new List<IPAddress>();
            foreach (var type in new[] { QueryType.A, QueryType.AAAA })
            {
                try
                {
                    var result = await dns.QueryAsync(host, type, cancellationToken: ct);
                    list.AddRange(result.Answers.OfType<AddressRecord>().Select(r => r.Address));
                }
                catch (DnsResponseException) { }
            }
            return list;
        }

        public async Task<IReadOnlyList<string>> GetMxHostsAsync(string domain, CancellationToken ct)
        {
            try
            {
                var result = await dns.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
                return [.. result.Answers.OfType<MxRecord>().OrderBy(m => m.Preference).Select(m => m.Exchange.Value.TrimEnd('.'))];
            }
            catch (DnsResponseException)
            {
                return [];
            }
        }
    }
}

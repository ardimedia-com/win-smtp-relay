using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using Nager.EmailAuthentication;
using Nager.EmailAuthentication.Models.Dmarc;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security.Models;
using DmarcPolicyLocal = WinSmtpRelay.Security.Models.DmarcPolicy;
using NagerDmarcPolicy = Nager.EmailAuthentication.Models.Dmarc.DmarcPolicy;

namespace WinSmtpRelay.Security;

public class DmarcValidator
{
    private readonly ILookupClient _dns;
    private readonly IPublicSuffixService _psl;
    private readonly ILogger<DmarcValidator> _logger;

    public DmarcValidator(ILookupClient dns, IPublicSuffixService psl, ILogger<DmarcValidator> logger)
    {
        _dns = dns;
        _psl = psl;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates DMARC for a received message. DMARC passes when EITHER SPF passes and the envelope-from
    /// aligns with the From domain, OR a DKIM signature verifies and its <c>d=</c> aligns with the From
    /// domain. Pass <paramref name="dkim"/> (the verified DKIM result) so the DKIM-alignment path is
    /// considered — without it only the SPF path can pass, which fails legitimate mail that authenticates
    /// solely via DKIM (a different/relayed envelope-from). Alignment uses the Public Suffix List, so
    /// multi-label suffixes (e.g. <c>co.uk</c>) are handled correctly.
    /// </summary>
    public async Task<DmarcCheckResult> CheckAsync(
        string headerFromDomain,
        string envelopeFromDomain,
        SpfCheckResult spfResult,
        DkimCheckResult? dkim = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(headerFromDomain))
            return new DmarcCheckResult(DmarcVerdict.None, DmarcPolicyLocal.None, "no From domain");

        try
        {
            // RFC 7489 §6.6.3: the From domain first, then its organizational domain. A record found at
            // the organizational domain governs a subdomain through its sp= (which defaults to p=).
            var lookup = await GetDmarcRecordAsync(headerFromDomain, cancellationToken);
            var foundAtOrgDomain = false;
            if (lookup.Status == LookupStatus.NoRecord)
            {
                var orgDomain = _psl.GetRegistrableDomain(headerFromDomain) ?? headerFromDomain;
                if (!string.Equals(orgDomain, headerFromDomain, StringComparison.OrdinalIgnoreCase))
                {
                    lookup = await GetDmarcRecordAsync(orgDomain, cancellationToken);
                    foundAtOrgDomain = lookup.Status == LookupStatus.Found;
                }
            }

            // A DNS timeout or server failure is NOT "no record": the caller must not treat the domain as
            // unprotected (and reject on SPF alone), nor as protected — it can only try again later.
            if (lookup.Status == LookupStatus.TempError)
                return new DmarcCheckResult(DmarcVerdict.TempError, DmarcPolicyLocal.None, $"DNS error looking up the DMARC record of {headerFromDomain}");

            if (lookup.Status == LookupStatus.NoRecord)
                return new DmarcCheckResult(DmarcVerdict.None, DmarcPolicyLocal.None, $"no DMARC record for {headerFromDomain}");

            if (!DmarcRecordParser.TryParse(lookup.Record, out var dmarcRecordBase) || dmarcRecordBase is not DmarcRecordV1 dmarcRecord)
                return new DmarcCheckResult(DmarcVerdict.PermError, DmarcPolicyLocal.None, "invalid DMARC record");

            var policy = MapPolicy(foundAtOrgDomain ? dmarcRecord.SubdomainPolicy : dmarcRecord.DomainPolicy);
            var percentage = Math.Clamp(dmarcRecord.PolicyPercentage, 0, 100);

            // DKIM-alignment pass: a verified signature whose d= aligns with the From domain. This holds
            // regardless of the envelope-from, so it is the path legitimate relayed/forwarded mail relies on.
            var strictDkim = dmarcRecord.DkimAlignmentMode == AlignmentMode.Strict;
            if (dkim is { Verdict: DkimVerdict.Pass } &&
                EnvelopeAlignment.IsAligned(dkim.SigningDomain, headerFromDomain, _psl, strictDkim))
            {
                return new DmarcCheckResult(DmarcVerdict.Pass, policy, $"DKIM aligned for {headerFromDomain} (d={dkim.SigningDomain})", percentage);
            }

            // SPF-alignment pass: SPF passes AND the envelope-from aligns with the From domain.
            var strictSpf = dmarcRecord.SpfAlignmentMode == AlignmentMode.Strict;
            if (spfResult.Verdict == SpfVerdict.Pass &&
                EnvelopeAlignment.IsAligned(envelopeFromDomain, headerFromDomain, _psl, strictSpf))
            {
                return new DmarcCheckResult(DmarcVerdict.Pass, policy, $"SPF aligned for {headerFromDomain}", percentage);
            }

            return new DmarcCheckResult(DmarcVerdict.Fail, policy,
                $"not aligned for {headerFromDomain} (spf={spfResult.Verdict}, envelope={envelopeFromDomain}, dkim={dkim?.Verdict.ToString() ?? "none"})",
                percentage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DMARC check error for {Domain}", headerFromDomain);
            return new DmarcCheckResult(DmarcVerdict.TempError, DmarcPolicyLocal.None, $"error: {ex.Message}");
        }
    }

    private static DmarcPolicyLocal MapPolicy(NagerDmarcPolicy policy) => policy switch
    {
        NagerDmarcPolicy.Reject => DmarcPolicyLocal.Reject,
        NagerDmarcPolicy.Quarantine => DmarcPolicyLocal.Quarantine,
        _ => DmarcPolicyLocal.None
    };

    private enum LookupStatus
    {
        Found,
        NoRecord,
        TempError
    }

    private readonly record struct DmarcLookup(LookupStatus Status, string? Record = null);

    /// <summary>
    /// Fetches the <c>v=DMARC1</c> TXT record at <c>_dmarc.&lt;domain&gt;</c>. NXDOMAIN and an answer without
    /// such a record mean "no record"; a timeout, SERVFAIL or REFUSED means "could not tell".
    /// </summary>
    private async Task<DmarcLookup> GetDmarcRecordAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _dns.QueryAsync($"_dmarc.{domain}", QueryType.TXT, cancellationToken: cancellationToken);
            if (result.HasError && result.Header?.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
                return new DmarcLookup(LookupStatus.TempError);

            var record = (result.Answers ?? [])
                .OfType<TxtRecord>()
                .Select(txt => string.Join("", txt.Text))
                .FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));

            return record is null ? new DmarcLookup(LookupStatus.NoRecord) : new DmarcLookup(LookupStatus.Found, record);
        }
        catch (DnsResponseException ex) when (ex.Code == DnsResponseCode.NotExistentDomain)
        {
            return new DmarcLookup(LookupStatus.NoRecord);
        }
        catch (DnsResponseException ex)
        {
            _logger.LogDebug(ex, "DMARC lookup for {Domain} failed", domain);
            return new DmarcLookup(LookupStatus.TempError);
        }
    }
}

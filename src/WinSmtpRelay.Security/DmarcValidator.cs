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
            var dmarcRaw = await GetDmarcRecordAsync(headerFromDomain, cancellationToken);
            if (dmarcRaw is null)
            {
                var orgDomain = _psl.GetRegistrableDomain(headerFromDomain) ?? headerFromDomain;
                if (orgDomain != headerFromDomain)
                    dmarcRaw = await GetDmarcRecordAsync(orgDomain, cancellationToken);
            }

            if (dmarcRaw is null)
                return new DmarcCheckResult(DmarcVerdict.None, DmarcPolicyLocal.None, $"no DMARC record for {headerFromDomain}");

            if (!DmarcRecordParser.TryParse(dmarcRaw, out var dmarcRecordBase) || dmarcRecordBase is not DmarcRecordV1 dmarcRecord)
                return new DmarcCheckResult(DmarcVerdict.PermError, DmarcPolicyLocal.None, "invalid DMARC record");

            var policy = dmarcRecord.DomainPolicy switch
            {
                NagerDmarcPolicy.Reject => DmarcPolicyLocal.Reject,
                NagerDmarcPolicy.Quarantine => DmarcPolicyLocal.Quarantine,
                _ => DmarcPolicyLocal.None
            };

            // DKIM-alignment pass: a verified signature whose d= aligns with the From domain. This holds
            // regardless of the envelope-from, so it is the path legitimate relayed/forwarded mail relies on.
            var strictDkim = dmarcRecord.DkimAlignmentMode == AlignmentMode.Strict;
            if (dkim is { Verdict: DkimVerdict.Pass } &&
                EnvelopeAlignment.IsAligned(dkim.SigningDomain, headerFromDomain, _psl, strictDkim))
            {
                return new DmarcCheckResult(DmarcVerdict.Pass, policy, $"DKIM aligned for {headerFromDomain} (d={dkim.SigningDomain})");
            }

            // SPF-alignment pass: SPF passes AND the envelope-from aligns with the From domain.
            var strictSpf = dmarcRecord.SpfAlignmentMode == AlignmentMode.Strict;
            if (spfResult.Verdict == SpfVerdict.Pass &&
                EnvelopeAlignment.IsAligned(envelopeFromDomain, headerFromDomain, _psl, strictSpf))
            {
                return new DmarcCheckResult(DmarcVerdict.Pass, policy, $"SPF aligned for {headerFromDomain}");
            }

            return new DmarcCheckResult(DmarcVerdict.Fail, policy,
                $"not aligned for {headerFromDomain} (spf={spfResult.Verdict}, envelope={envelopeFromDomain}, dkim={dkim?.Verdict.ToString() ?? "none"})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DMARC check error for {Domain}", headerFromDomain);
            return new DmarcCheckResult(DmarcVerdict.TempError, DmarcPolicyLocal.None, $"error: {ex.Message}");
        }
    }

    private async Task<string?> GetDmarcRecordAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _dns.QueryAsync($"_dmarc.{domain}", QueryType.TXT, cancellationToken: cancellationToken);
            return result.Answers
                .OfType<TxtRecord>()
                .Select(txt => string.Join("", txt.Text))
                .FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
        }
        catch (DnsResponseException)
        {
            return null;
        }
    }
}

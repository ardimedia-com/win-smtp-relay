using System.Net;
using Microsoft.Extensions.Logging;
using MimeKit;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Security.Models;

namespace WinSmtpRelay.Security;

public class EmailAuthenticationService
{
    private readonly SpfValidator _spf;
    private readonly DmarcValidator _dmarc;
    private readonly InboundDkimVerifier _dkim;
    private readonly IRuntimeConfigCache _configCache;
    private readonly ILogger<EmailAuthenticationService> _logger;

    public EmailAuthenticationService(
        SpfValidator spf,
        DmarcValidator dmarc,
        InboundDkimVerifier dkim,
        IRuntimeConfigCache configCache,
        ILogger<EmailAuthenticationService> logger)
    {
        _spf = spf;
        _dmarc = dmarc;
        _dkim = dkim;
        _configCache = configCache;
        _logger = logger;
    }

    public async Task<SpfCheckResult> CheckSpfAsync(
        IPAddress senderIp,
        string mailFromDomain,
        CancellationToken cancellationToken = default)
    {
        var settings = await _configCache.GetEmailAuthSettingsAsync(cancellationToken);
        if (!settings.SpfEnabled)
            return new SpfCheckResult(SpfVerdict.None, "SPF checking disabled");

        var result = await _spf.CheckAsync(senderIp, mailFromDomain, cancellationToken);

        _logger.LogInformation("SPF check for {Domain} from {Ip}: {Verdict} ({Explanation})",
            mailFromDomain, senderIp, result.Verdict, result.Explanation);

        return result;
    }

    public async Task<AuthenticationResults> CheckAllAsync(
        IPAddress senderIp,
        string envelopeFromDomain,
        string headerFromDomain,
        MimeMessage? message = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _configCache.GetEmailAuthSettingsAsync(cancellationToken);
        var spfResult = await CheckSpfAsync(senderIp, envelopeFromDomain, cancellationToken);

        // DKIM is verified only as part of DMARC evaluation (it feeds the DKIM-alignment pass path) and
        // only when the raw message is available. Without it, DMARC can pass solely via SPF alignment.
        var dkimResult = settings.DmarcEnabled && message is not null
            ? await _dkim.VerifyAsync(message, cancellationToken)
            : null;

        var dmarcResult = settings.DmarcEnabled
            ? await _dmarc.CheckAsync(headerFromDomain, envelopeFromDomain, spfResult, dkimResult, cancellationToken)
            : new DmarcCheckResult(DmarcVerdict.None, DmarcPolicy.None, "DMARC checking disabled");

        if (dmarcResult.Verdict != DmarcVerdict.None)
        {
            _logger.LogInformation("DMARC check for {Domain}: {Verdict} policy={Policy} ({Explanation})",
                headerFromDomain, dmarcResult.Verdict, dmarcResult.Policy, dmarcResult.Explanation);
        }

        return new AuthenticationResults(spfResult, dmarcResult, dkimResult);
    }

    public async Task<bool> ShouldRejectAsync(AuthenticationResults results, CancellationToken cancellationToken = default)
    {
        var settings = await _configCache.GetEmailAuthSettingsAsync(cancellationToken);
        return settings.Enforcement == EnforcementMode.Reject && results.ShouldReject;
    }

    public async Task<bool> ShouldQuarantineAsync(AuthenticationResults results, CancellationToken cancellationToken = default)
    {
        var settings = await _configCache.GetEmailAuthSettingsAsync(cancellationToken);
        return settings.Enforcement == EnforcementMode.Quarantine && results.ShouldQuarantine;
    }
}

namespace WinSmtpRelay.Security.Models;

public record AuthenticationResults(SpfCheckResult Spf, DmarcCheckResult Dmarc, DkimCheckResult? Dkim = null)
{
    public string ToHeaderValue(string authservId)
    {
        var parts = new List<string> { authservId };

        if (Spf.Verdict != SpfVerdict.None)
            parts.Add($"spf={Spf.Verdict.ToString().ToLowerInvariant()} ({Spf.Explanation})");

        if (Dkim is not null && Dkim.Verdict != DkimVerdict.None)
        {
            var dkim = $"dkim={Dkim.Verdict.ToString().ToLowerInvariant()} ({Dkim.Explanation})";
            if (!string.IsNullOrWhiteSpace(Dkim.SigningDomain))
                dkim += $" header.d={Dkim.SigningDomain}";
            parts.Add(dkim);
        }

        if (Dmarc.Verdict != DmarcVerdict.None)
            parts.Add($"dmarc={Dmarc.Verdict.ToString().ToLowerInvariant()} ({Dmarc.Explanation})");

        return string.Join(";\r\n\t", parts);
    }

    /// <summary>
    /// The domain owner's own policy decides, not SPF on its own:
    /// <list type="bullet">
    /// <item>DMARC failed and the applicable policy (<c>p=</c>, or <c>sp=</c> for a subdomain) is
    /// <c>reject</c> at <c>pct=100</c>. A record with <c>pct</c> below 100 is never rejected outright:
    /// the domain is still ramping its policy up, and RFC 7489 §6.6.4 applies the next-lower policy
    /// (quarantine) to the part not sampled — rejecting a random share of its mail would lose legitimate
    /// mail the owner did not ask us to lose. That is a deliberate, documented choice.</item>
    /// <item>SPF failed hard (<c>-all</c>) and the From domain publishes NO DMARC record (or DMARC checking
    /// is disabled). Where a record exists, it governs: a DKIM-aligned pass overrides an SPF fail, which is
    /// exactly the forwarded-mail case.</item>
    /// </list>
    /// </summary>
    public bool ShouldReject =>
        (Dmarc.Verdict == DmarcVerdict.Fail && Dmarc.Policy == DmarcPolicy.Reject && Dmarc.Percentage >= 100)
        || (Spf.Verdict == SpfVerdict.Fail && Dmarc.Verdict == DmarcVerdict.None);

    /// <summary>
    /// SPF failed hard but whether the From domain has a DMARC record could not be determined (a DNS
    /// timeout or server failure). "No answer" is not "no record", so the message is neither rejected on
    /// SPF alone nor accepted: it is deferred with a 4xx and the sending MTA retries.
    /// </summary>
    public bool ShouldDefer =>
        Spf.Verdict == SpfVerdict.Fail && Dmarc.Verdict == DmarcVerdict.TempError;

    /// <summary>
    /// DMARC failed under a <c>quarantine</c> policy (or under a <c>reject</c> policy with <c>pct</c> below
    /// 100, see <see cref="ShouldReject"/>), or — without any DMARC record — SPF failed or soft-failed.
    /// </summary>
    public bool ShouldQuarantine =>
        (Dmarc.Verdict == DmarcVerdict.Fail
            && (Dmarc.Policy == DmarcPolicy.Quarantine || (Dmarc.Policy == DmarcPolicy.Reject && Dmarc.Percentage < 100)))
        || (Dmarc.Verdict == DmarcVerdict.None && Spf.Verdict is SpfVerdict.SoftFail or SpfVerdict.Fail);
}

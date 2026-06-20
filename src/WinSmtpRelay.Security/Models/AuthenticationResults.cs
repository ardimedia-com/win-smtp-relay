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

    public bool ShouldReject =>
        Spf.Verdict == SpfVerdict.Fail || Dmarc.Verdict == DmarcVerdict.Fail;

    public bool ShouldQuarantine =>
        Spf.Verdict == SpfVerdict.SoftFail || Dmarc.Policy == DmarcPolicy.Quarantine;
}

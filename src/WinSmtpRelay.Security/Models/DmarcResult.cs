namespace WinSmtpRelay.Security.Models;

public enum DmarcVerdict
{
    None,
    Pass,
    Fail,
    TempError,
    PermError
}

public enum DmarcPolicy
{
    None,
    Quarantine,
    Reject
}

/// <summary>
/// Result of a DMARC evaluation. <see cref="Policy"/> is the policy that APPLIES to the From domain: the
/// record's <c>p=</c> when the record was published at the From domain itself, its <c>sp=</c> (which
/// defaults to <c>p=</c>) when the From domain is a subdomain and the record was found at the
/// organizational domain. <see cref="Percentage"/> is the record's <c>pct=</c> (100 when absent).
/// </summary>
public record DmarcCheckResult(DmarcVerdict Verdict, DmarcPolicy Policy, string Explanation, int Percentage = 100);

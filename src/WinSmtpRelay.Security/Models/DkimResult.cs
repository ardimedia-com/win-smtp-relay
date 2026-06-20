namespace WinSmtpRelay.Security.Models;

public enum DkimVerdict
{
    /// <summary>No DKIM signature was present (or DKIM was not checked).</summary>
    None,
    /// <summary>At least one DKIM signature verified cryptographically.</summary>
    Pass,
    /// <summary>A signature was present but none verified.</summary>
    Fail,
    /// <summary>A transient error (e.g. the public key could not be fetched) prevented verification.</summary>
    TempError,
    /// <summary>A permanent error (e.g. a malformed signature) prevented verification.</summary>
    PermError
}

/// <summary>
/// Result of verifying the DKIM signature(s) on a message. <see cref="SigningDomain"/> is the <c>d=</c>
/// of the signature that determined the verdict — used for DMARC DKIM-alignment against the From domain.
/// </summary>
public record DkimCheckResult(DkimVerdict Verdict, string? SigningDomain, string Explanation);

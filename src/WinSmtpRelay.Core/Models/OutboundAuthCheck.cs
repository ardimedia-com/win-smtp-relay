namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Result of the local outbound authentication check: the relay builds the exact message it would send
/// for a From address, DKIM-signs it, then verifies the result against its own key and the published DNS —
/// without sending anything. Proves end-to-end that the signature is valid and aligned, and synthesises the
/// DMARC outcome a receiver would reach.
/// </summary>
/// <param name="FromAddress">The From address that was tested.</param>
/// <param name="Domain">The From domain.</param>
/// <param name="DkimSigned">A DKIM-Signature header was produced (a key is configured + enabled for the domain).</param>
/// <param name="DkimSignatureValid">The produced signature verified cryptographically against the relay's own key.</param>
/// <param name="DkimSigningDomain">The signature's <c>d=</c> value.</param>
/// <param name="DkimAligned">The signature's <c>d=</c> aligns with the From domain.</param>
/// <param name="Alignment">The DNS-derived DMARC-alignment synthesis for the domain (SPF/DKIM/DMARC published state).</param>
/// <param name="Notes">Human-readable findings, in display order.</param>
public sealed record OutboundAuthCheck(
    string FromAddress,
    string Domain,
    bool DkimSigned,
    bool DkimSignatureValid,
    string? DkimSigningDomain,
    bool DkimAligned,
    DmarcAlignment Alignment,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Overall verdict: the message will reach DMARC pass at a receiver. True when the produced DKIM
    /// signature is valid and aligned (robust), since that passes DMARC independent of the envelope-from.
    /// </summary>
    public bool WillPassDmarc => DkimSigned && DkimSignatureValid && DkimAligned;
}

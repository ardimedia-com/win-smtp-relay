namespace WinSmtpRelay.Core.Models;

/// <summary>
/// How a sender domain will fare under DMARC when mail is sent through this relay. DMARC passes when
/// EITHER DKIM passes and its signing domain (<c>d=</c>) aligns with the From domain, OR SPF passes and
/// the envelope-from (Return-Path) domain aligns with the From domain. A green SPF record alone is NOT
/// enough — the published-record checks each verify one leg in isolation; this verdict is the synthesis
/// that answers "will a message from this domain actually reach DMARC pass?".
/// </summary>
public enum DmarcAlignmentVerdict
{
    /// <summary>
    /// DKIM is configured, published and matching for this domain. Because the relay always signs with
    /// <c>d=</c> equal to the From domain, DKIM is aligned by construction — so DMARC passes for every
    /// message regardless of the envelope-from the submitting app uses. This is the robust outcome.
    /// </summary>
    DkimAligned,

    /// <summary>
    /// No DKIM, but the published SPF record authorises this relay. DMARC then passes ONLY when the
    /// submitting app's envelope-from (Return-Path) is on the From domain (SPF alignment). A different
    /// bounce domain — common with web apps and bulk senders — fails DMARC despite a green SPF record.
    /// Set up DKIM to make alignment robust.
    /// </summary>
    SpfConditional,

    /// <summary>
    /// Neither DKIM alignment nor SPF authorisation is in place — DMARC will fail. If the domain
    /// publishes <c>p=quarantine</c>/<c>p=reject</c>, mail is spam-foldered or rejected.
    /// </summary>
    Fail
}

/// <summary>
/// Synthesised DMARC-alignment outcome for one sender domain, derived purely from the three published-record
/// checks (SPF / DKIM / DMARC). Pure domain logic — no infrastructure dependency.
/// </summary>
/// <param name="Verdict">The overall how-will-DMARC-pass outcome.</param>
/// <param name="DkimAligned">DKIM is configured + published + matching (so <c>d=</c> aligns with From by construction).</param>
/// <param name="SpfAuthorized">The published SPF record authorises this relay (alignment still depends on the envelope-from).</param>
/// <param name="DmarcPublished">A DMARC record is published, so receivers actually evaluate and enforce a policy.</param>
/// <param name="Summary">A one-line, human-readable explanation suitable for the UI.</param>
public sealed record DmarcAlignment(
    DmarcAlignmentVerdict Verdict,
    bool DkimAligned,
    bool SpfAuthorized,
    bool DmarcPublished,
    string Summary)
{
    public static DmarcAlignment Evaluate(DnsRecordResult spf, DnsRecordResult dkim, DnsRecordResult dmarc)
    {
        var dkimAligned = dkim.Status == DnsRecordStatus.Ok;
        var spfAuthorized = spf.Status == DnsRecordStatus.Ok;
        var dmarcPublished = dmarc.Status == DnsRecordStatus.Ok;

        var verdict = dkimAligned ? DmarcAlignmentVerdict.DkimAligned
            : spfAuthorized ? DmarcAlignmentVerdict.SpfConditional
            : DmarcAlignmentVerdict.Fail;

        // The DMARC-record state only changes whether receivers enforce a policy; it does not change which
        // authentication leg aligns. Mention it so a "passes, but no policy published" gap is visible.
        var policyNote = dmarcPublished
            ? ""
            : " No DMARC record is published yet, so receivers won't enforce a policy — publish one once alignment is in place.";

        var summary = verdict switch
        {
            DmarcAlignmentVerdict.DkimAligned =>
                "DMARC passes via DKIM (d= aligns with the From domain) for every message, independent of the envelope-from."
                + policyNote,
            DmarcAlignmentVerdict.SpfConditional =>
                "DMARC passes only when the sending app's envelope-from (Return-Path) is on this domain (SPF alignment). "
                + "A different bounce domain fails DMARC despite the green SPF record — set up DKIM to make it robust."
                + policyNote,
            _ =>
                "DMARC will fail: neither DKIM (set up a key for this domain) nor SPF authorisation for this relay is in place."
                + policyNote,
        };

        return new DmarcAlignment(verdict, dkimAligned, spfAuthorized, dmarcPublished, summary);
    }
}

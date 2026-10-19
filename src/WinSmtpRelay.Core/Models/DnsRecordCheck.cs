namespace WinSmtpRelay.Core.Models;

public enum DnsRecordStatus
{
    /// <summary>No expected value can be produced yet (e.g. DKIM not set up, or SPF identity not configured).</summary>
    NotConfigured,
    /// <summary>The published record matches the expected/recommended value.</summary>
    Ok,
    /// <summary>An expected value exists but nothing is published.</summary>
    Missing,
    /// <summary>A record is published but differs from the expected value.</summary>
    Mismatch,
    /// <summary>The item is present on a blocklist (DNSBL) — a bad outcome, unlike the others.</summary>
    Listed,
    /// <summary>
    /// The DNS lookup failed (no resolver answered — timeout, SERVFAIL, network down), so the record could not be
    /// verified. Says nothing about whether it is published; never to be read as "missing".
    /// </summary>
    Unverified
}

/// <summary>One DNS record (SPF/DKIM/DMARC) for a domain: where to publish it, the expected value, the live value, and the diff status.</summary>
/// <param name="Suggested">
/// An optional ready-to-publish value that accounts for what is already published — used for SPF, where
/// only one record may exist per domain: the published record merged with the relay's missing mechanisms,
/// so the operator can copy a single complete record instead of replacing (and breaking) the existing one.
/// </param>
public record DnsRecordResult(
    string RecordType,
    string RecordName,
    string Expected,
    string? Live,
    DnsRecordStatus Status,
    string Explanation,
    string? Suggested = null);

/// <summary>
/// The smart-host side of a sender domain whose mail all leaves through a smart host: which hosts, which known
/// provider runs them (null = not a known provider), and the provider's DKIM selector found published for the
/// domain (null = none of its selectors is published).
/// </summary>
/// <param name="LookupFailed">A selector lookup failed (no resolver answered), so "none published" is not proven.</param>
public record SmartHostDkim(string SmartHosts, string? Provider, string? PublishedSelector, IReadOnlyList<string> Selectors, bool LookupFailed = false);

/// <summary>The full DNS-setup picture for one sender domain.</summary>
/// <param name="SmartHost">Set when every message of the domain's tenant leaves through a smart host.</param>
public record DomainDnsSetup(string Domain, DnsRecordResult Spf, DnsRecordResult Dkim, DnsRecordResult Dmarc, SmartHostDkim? SmartHost = null)
{
    /// <summary>
    /// The synthesised DMARC-alignment outcome for this domain — the answer to "will mail from this domain,
    /// sent through this relay, actually reach DMARC pass?". Derived purely from the three record checks
    /// above (it is the missing synthesis the per-leg checks don't give on their own), and from the smart
    /// host's DKIM signature when the mail leaves through one.
    /// </summary>
    public DmarcAlignment Alignment => DmarcAlignment.Evaluate(Spf, Dkim, Dmarc, SmartHost);
}

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
    Listed
}

/// <summary>One DNS record (SPF/DKIM/DMARC) for a domain: where to publish it, the expected value, the live value, and the diff status.</summary>
public record DnsRecordResult(
    string RecordType,
    string RecordName,
    string Expected,
    string? Live,
    DnsRecordStatus Status,
    string Explanation);

/// <summary>The full DNS-setup picture for one sender domain.</summary>
public record DomainDnsSetup(string Domain, DnsRecordResult Spf, DnsRecordResult Dkim, DnsRecordResult Dmarc);

namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Host-level inputs for the recommended SPF/DMARC records shown on the Health page
/// (single row), runtime-editable. Seeded once from appsettings <c>Dns</c>, then authoritative.
/// Consumed only by the Health page (not the SMTP hot path), so it is read directly.
/// Lists are stored as semicolon/comma-delimited strings.
/// </summary>
public class DnsSettings
{
    public int Id { get; set; }

    public string? PublicHostname { get; set; }

    /// <summary>Public egress IPs (delimited), emitted as SPF ip4:/ip6:.</summary>
    public string SendingIpAddresses { get; set; } = "";

    /// <summary>Additional SPF include: domains (delimited).</summary>
    public string SpfIncludes { get; set; } = "";

    /// <summary>SPF "all" qualifier: ~all or -all.</summary>
    public string SpfAllQualifier { get; set; } = "~all";

    public string? DmarcReportEmail { get; set; }

    /// <summary>Recommended DMARC policy: none | quarantine | reject.</summary>
    public string DmarcPolicy { get; set; } = "none";

    /// <summary>Recommended DMARC percentage (1-100).</summary>
    public int DmarcPercentage { get; set; } = 100;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

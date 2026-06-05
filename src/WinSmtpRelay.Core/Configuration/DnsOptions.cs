namespace WinSmtpRelay.Core.Configuration;

/// <summary>
/// Inputs for building the RECOMMENDED SPF/DMARC records shown on the Health page.
/// Live records are always resolved from DNS; these only describe what the operator should publish.
/// </summary>
public class DnsOptions
{
    public const string SectionName = "Dns";

    /// <summary>Relay's public sending hostname (emitted as an SPF <c>a:</c> mechanism), e.g. relay.example.com.</summary>
    public string? PublicHostname { get; set; }

    /// <summary>Public egress IPs the relay sends from (emitted as SPF <c>ip4:</c>/<c>ip6:</c>).</summary>
    public List<string> SendingIpAddresses { get; set; } = [];

    /// <summary>Additional SPF <c>include:</c> domains.</summary>
    public List<string> SpfIncludes { get; set; } = [];

    /// <summary>SPF "all" qualifier: <c>~all</c> (softfail) or <c>-all</c> (fail).</summary>
    public string SpfAllQualifier { get; set; } = "~all";

    /// <summary>Address for DMARC aggregate reports (<c>rua=mailto:</c>). Omitted from the recommendation when empty.</summary>
    public string? DmarcReportEmail { get; set; }

    /// <summary>Recommended DMARC policy: none | quarantine | reject.</summary>
    public string DmarcPolicy { get; set; } = "none";

    /// <summary>Recommended DMARC percentage (1-100).</summary>
    public int DmarcPercentage { get; set; } = 100;
}

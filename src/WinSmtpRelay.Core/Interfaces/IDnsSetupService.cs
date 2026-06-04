using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Computes the required vs. live DNS records (SPF/DKIM/DMARC) for sender domains so operators
/// can see what to publish. Lives in Core (interface only) so the Blazor UI can resolve it
/// without referencing the Security implementation.
/// </summary>
public interface IDnsSetupService
{
    Task<DomainDnsSetup> CheckDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>Checks every accepted sender domain in the current tenant scope.</summary>
    Task<IReadOnlyList<DomainDnsSetup>> CheckAllSenderDomainsAsync(CancellationToken ct = default);

    /// <summary>The recommended SPF record value, built from <see cref="Configuration.DnsOptions"/>.</summary>
    string BuildRecommendedSpf();

    /// <summary>The recommended DMARC record value for a domain, built from <see cref="Configuration.DnsOptions"/>.</summary>
    string BuildRecommendedDmarc(string domain);

    /// <summary>The TXT record value a tenant must publish at the domain apex to prove ownership.</summary>
    string BuildOwnershipRecord(string token);

    /// <summary>True if a TXT record matching <see cref="BuildOwnershipRecord"/> for the token is published at the domain.</summary>
    Task<bool> CheckOwnershipAsync(string domain, string token, CancellationToken ct = default);
}

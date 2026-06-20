using DnsClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Security;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the security engine: SPF/DKIM/DMARC validation and signing, deliverability DNS checks,
    /// the local outbound authentication self-test, the Public Suffix List, and the DNS resolvers. Every
    /// registration is idempotent (<c>TryAdd</c>), so it is safe to call this directly and to have the
    /// delivery engine and SMTP listener pull it in as a dependency. Depends only on Core ports — the
    /// implementations behind those ports (e.g. <see cref="IDkimDomainService"/>) come from the storage layer.
    /// </summary>
    public static IServiceCollection AddRelaySecurity(this IServiceCollection services)
    {
        // DNS resolvers: the host's own resolver for general lookups, and a public resolver (8.8.8.8 /
        // 1.1.1.1) for record checks that must avoid split-horizon answers.
        services.TryAddSingleton<ILookupClient>(new LookupClient());
        services.TryAddSingleton<PublicDnsLookupClient>();

        // Registrable-domain derivation (embedded snapshot, parsed once).
        services.TryAddSingleton<IPublicSuffixService, PublicSuffixService>();

        // Inbound authentication (SPF/DKIM/DMARC) + the orchestrator.
        services.TryAddSingleton<SpfValidator>();
        services.TryAddSingleton<DmarcValidator>();
        services.TryAddSingleton<InboundDkimVerifier>();
        services.TryAddSingleton<EmailAuthenticationService>();

        // Outbound signing + the local "will this pass DMARC" self-test.
        services.TryAddSingleton<DkimSigningService>();
        services.TryAddScoped<IOutboundAuthCheckService, OutboundAuthCheckService>();

        // Deliverability DNS checks (SPF/DKIM/DMARC/MX/PTR/DNSBL) for the Setup and Deliverability pages.
        services.TryAddScoped<IDnsSetupService, DnsSetupService>();

        return services;
    }
}

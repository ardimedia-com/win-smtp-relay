using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmtpServer.Authentication;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.SmtpListener;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSmtpListener(this IServiceCollection services)
    {
        // NullActivityNotifier is the fallback; overridden by SignalR-backed ActivityNotifier when Admin UI is enabled
        services.TryAddSingleton<IActivityNotifier, NullActivityNotifier>();
        services.AddSingleton<CertificateLoader>();
        services.AddSingleton<RelayMessageStore>();
        services.AddSingleton<RelayMailboxFilter>();
        services.AddSingleton<IUserAuthenticator, RelayUserAuthenticator>();
        services.AddSingleton<SpfValidator>();
        services.AddSingleton<DmarcValidator>();
        services.AddSingleton<InboundDkimVerifier>();
        services.AddSingleton<EmailAuthenticationService>();
        // DmarcValidator needs registrable-domain (Public Suffix List) lookup for alignment. The Service host
        // also registers this; TryAdd keeps a single instance and lets test/alternate hosts resolve it too.
        services.TryAddSingleton<IPublicSuffixService, PublicSuffixService>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<WebhookService>();
        services.AddHttpClient("Webhook");
        services.AddHostedService<SmtpRelayServer>();
        services.AddHostedService<PickupFolderService>();

        return services;
    }
}

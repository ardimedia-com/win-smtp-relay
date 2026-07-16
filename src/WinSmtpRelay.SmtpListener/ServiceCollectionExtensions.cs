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
        // The inbound path authenticates mail (SPF/DKIM/DMARC) via the security engine.
        services.AddRelaySecurity();

        // NullActivityNotifier is the fallback; overridden by SignalR-backed ActivityNotifier when Admin UI is enabled
        services.TryAddSingleton<IActivityNotifier, NullActivityNotifier>();
        services.AddSingleton<CertificateLoader>();
        services.AddSingleton<RelayMessageStore>();
        services.AddSingleton<RelayMailboxFilter>();
        services.AddSingleton<IUserAuthenticator, RelayUserAuthenticator>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<WebhookService>();
        services.AddHttpClient("Webhook");

        // Observable rejections: one instance is both the hot-path aggregate (IRejectionRecorder) and the
        // background flush (BackgroundService), so both registrations must resolve the SAME object —
        // hence the singleton plus two forwarding registrations, not three independent ones.
        services.AddSingleton<RejectionRecorder>();
        services.AddSingleton<IRejectionRecorder>(sp => sp.GetRequiredService<RejectionRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<RejectionRecorder>());

        services.AddHostedService<SmtpRelayServer>();
        services.AddHostedService<PickupFolderService>();

        return services;
    }
}

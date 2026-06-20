using DnsClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Delivery.Filters;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Delivery;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDeliveryEngine(this IServiceCollection services)
    {
        services.AddSingleton<ILookupClient>(new LookupClient());
        services.AddSingleton<IMxResolver, MxResolver>();
        services.AddSingleton<DkimSigningService>();
        // Registrable-domain (Public Suffix List) lookup for envelope-from alignment. TryAdd so it
        // coexists with the Service host's registration and lets alternate hosts resolve it too.
        services.TryAddSingleton<IPublicSuffixService, PublicSuffixService>();
        services.AddScoped<IDeliveryService, SmtpDeliveryService>();
        services.AddHostedService<DeliveryWorker>();

        // Message filters (chain of responsibility)
        services.AddSingleton<IMessageFilter, HeaderRewriteFilter>();
        services.AddSingleton<IMessageFilter, SenderRewriteFilter>();

        return services;
    }
}

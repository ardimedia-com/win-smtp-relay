using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Delivery.Filters;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Delivery;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDeliveryEngine(this IServiceCollection services)
    {
        // DKIM signing, the DNS resolver (ILookupClient), and the public-suffix lookup used for
        // envelope-from alignment all come from the security engine.
        services.AddRelaySecurity();

        services.AddSingleton<IMxResolver, MxResolver>();
        services.AddScoped<IDeliveryService, SmtpDeliveryService>();
        services.AddHostedService<DeliveryWorker>();

        // Message filters (chain of responsibility)
        services.AddSingleton<IMessageFilter, HeaderRewriteFilter>();
        services.AddSingleton<IMessageFilter, SenderRewriteFilter>();

        return services;
    }
}

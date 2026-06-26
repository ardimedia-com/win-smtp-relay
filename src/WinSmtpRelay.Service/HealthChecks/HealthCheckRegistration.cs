using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Service.HealthChecks.Checks;

namespace WinSmtpRelay.Service.HealthChecks;

public static class HealthCheckRegistration
{
    /// <summary>
    /// Registers the daily self-check: every <see cref="IHealthCheck"/> (scoped, resolved together per run)
    /// and the <see cref="IHealthCheckRunner"/> (singleton — it creates its own host-wide scope per run).
    /// The hosted <see cref="HealthCheckService"/> and the <c>HealthCheckOptions</c> binding are wired in
    /// <c>Program.cs</c> alongside the other reporting services.
    /// </summary>
    public static IServiceCollection AddRelayHealthChecks(this IServiceCollection services)
    {
        services.AddScoped<IHealthCheck, DeliverabilityHealthCheck>();
        services.AddScoped<IHealthCheck, ConnectivityHealthCheck>();
        services.AddScoped<IHealthCheck, CertificateHealthCheck>();
        services.AddScoped<IHealthCheck, ConfigurationHealthCheck>();
        services.AddScoped<IHealthCheck, QueueHealthCheck>();
        services.AddScoped<IHealthCheck, SecurityHealthCheck>();
        services.AddScoped<IHealthCheck, RuntimeHealthCheck>();

        services.AddSingleton<IHealthCheckRunner, HealthCheckRunner>();
        return services;
    }
}

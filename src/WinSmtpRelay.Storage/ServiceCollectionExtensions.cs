using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRelayStorage(this IServiceCollection services, string connectionString)
    {
        // Ambient tenant for query filtering (set per request/circuit; unset = no filter).
        services.AddScoped<ICurrentTenant, CurrentTenant>();
        services.AddScoped<ITenantScopeFactory, TenantScopeFactory>();

        services.AddDbContext<RelayDbContext>(options =>
            options.UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly("WinSmtpRelay.Storage")));

        services.AddScoped<IMessageQueue, MessageQueue>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IStatisticsService, StatisticsService>();
        services.AddScoped<IApiKeyService, ApiKeyService>();
        services.AddScoped<ITenantService, TenantService>();

        // Configuration services
        services.AddScoped<IReceiveConnectorService, ReceiveConnectorService>();
        services.AddScoped<IAcceptedDomainService, AcceptedDomainService>();
        services.AddScoped<IAcceptedSenderDomainService, AcceptedSenderDomainService>();
        services.AddScoped<IIpAccessRuleService, IpAccessRuleService>();
        services.AddScoped<ISendConnectorService, SendConnectorService>();
        services.AddScoped<IDomainRouteService, DomainRouteService>();
        services.AddScoped<IDkimDomainService, DkimDomainService>();
        services.AddScoped<IRateLimitSettingsService, RateLimitSettingsService>();
        services.AddScoped<IPortalSettingsService, PortalSettingsService>();
        services.AddScoped<IEmailAuthSettingsService, EmailAuthSettingsService>();
        services.AddScoped<IBackupMxSettingsService, BackupMxSettingsService>();
        services.AddScoped<IMessageFilterService, MessageFilterService>();

        // Singleton cache for runtime-editable config (invalidated by Admin API)
        services.AddSingleton<IRuntimeConfigCache, RuntimeConfigCache>();

        return services;
    }
}

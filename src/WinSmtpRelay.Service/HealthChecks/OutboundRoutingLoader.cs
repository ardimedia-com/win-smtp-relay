using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Service.HealthChecks;

/// <summary>
/// Loads what <see cref="OutboundRouting.Evaluate"/> needs. Checks run in an unscoped DI scope, so the
/// services return every tenant's connectors and routes.
/// </summary>
public static class OutboundRoutingLoader
{
    public static async Task<OutboundRouting> LoadAsync(
        ITenantService tenants, ISendConnectorService connectors, IDomainRouteService routes,
        DeliveryOptions delivery, CancellationToken ct) =>
        OutboundRouting.Evaluate(
            await tenants.GetAllAsync(ct),
            await connectors.GetAllAsync(ct),
            await routes.GetAllAsync(ct),
            delivery.SmartHost);
}

using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class DomainRouteService(RelayDbContext db) : IDomainRouteService
{
    public async Task<IReadOnlyList<DomainRoute>> GetAllAsync(CancellationToken ct = default)
    {
        return await db.DomainRoutes
            .AsNoTracking()
            .Include(r => r.SendConnector)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct);
    }

    public async Task<DomainRoute> CreateAsync(DomainRoute route, CancellationToken ct = default)
    {
        await EnsureConnectorInScopeAsync(route.SendConnectorId, ct);
        db.DomainRoutes.Add(route);
        await db.SaveChangesAsync(ct);
        return route;
    }

    public async Task UpdateAsync(DomainRoute route, CancellationToken ct = default)
    {
        var existing = await db.DomainRoutes.FirstOrDefaultAsync(r => r.Id == route.Id, ct);
        if (existing is null) return;

        await EnsureConnectorInScopeAsync(route.SendConnectorId, ct);
        existing.DomainPattern = route.DomainPattern;
        existing.SendConnectorId = route.SendConnectorId;
        existing.SortOrder = route.SortOrder;

        await db.SaveChangesAsync(ct);
    }

    // A route must reference a send connector visible in the current tenant scope, so a tenant can't
    // route its mail through another tenant's smart host/credentials. The query filter scopes this to
    // the caller's tenant (host scope sees all connectors, which is intended for host admins).
    private async Task EnsureConnectorInScopeAsync(int sendConnectorId, CancellationToken ct)
    {
        if (!await db.SendConnectors.AnyAsync(c => c.Id == sendConnectorId, ct))
            throw new InvalidOperationException($"Send connector {sendConnectorId} was not found in the current scope.");
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await db.DomainRoutes.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }
}

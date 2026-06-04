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
        db.DomainRoutes.Add(route);
        await db.SaveChangesAsync(ct);
        return route;
    }

    public async Task UpdateAsync(DomainRoute route, CancellationToken ct = default)
    {
        var existing = await db.DomainRoutes.FirstOrDefaultAsync(r => r.Id == route.Id, ct);
        if (existing is null) return;

        existing.DomainPattern = route.DomainPattern;
        existing.SendConnectorId = route.SendConnectorId;
        existing.SortOrder = route.SortOrder;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await db.DomainRoutes.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }
}

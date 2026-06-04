using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class ReceiveConnectorService(RelayDbContext db) : IReceiveConnectorService
{
    public async Task<IReadOnlyList<ReceiveConnector>> GetAllAsync(CancellationToken ct = default)
    {
        return await db.ReceiveConnectors.AsNoTracking().OrderBy(c => c.Port).ToListAsync(ct);
    }

    public async Task<ReceiveConnector?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await db.ReceiveConnectors.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<ReceiveConnector> CreateAsync(ReceiveConnector connector, CancellationToken ct = default)
    {
        db.ReceiveConnectors.Add(connector);
        await db.SaveChangesAsync(ct);
        return connector;
    }

    public async Task UpdateAsync(ReceiveConnector connector, CancellationToken ct = default)
    {
        var existing = await db.ReceiveConnectors.FirstOrDefaultAsync(c => c.Id == connector.Id, ct);
        if (existing is null) return;

        existing.Name = connector.Name;
        existing.Address = connector.Address;
        existing.Port = connector.Port;
        existing.RequireTls = connector.RequireTls;
        existing.ImplicitTls = connector.ImplicitTls;
        existing.RequireAuth = connector.RequireAuth;
        existing.MaxMessageSizeBytes = connector.MaxMessageSizeBytes;
        existing.MaxConnections = connector.MaxConnections;
        existing.IsEnabled = connector.IsEnabled;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await db.ReceiveConnectors.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }
}

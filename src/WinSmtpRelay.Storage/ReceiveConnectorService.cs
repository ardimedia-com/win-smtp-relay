using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// Mutations here change the host's listening sockets (ports, TLS, auth requirements) — audited at the
// SERVICE so no caller can change the listener surface without leaving a trace.
public class ReceiveConnectorService(
    RelayDbContext db,
    ICurrentActor actor,
    IAdminAuditService audit) : IReceiveConnectorService
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
        await audit.WriteAsync(AdminAuditActions.ReceiveConnectorCreated, actor,
            detail: $"{connector.Name} {connector.Address}:{connector.Port}", ct: ct);
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
        await audit.WriteAsync(AdminAuditActions.ReceiveConnectorUpdated, actor,
            detail: $"{existing.Name} {existing.Address}:{existing.Port}", ct: ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        // Load-then-delete so the audit row can name the connector that was removed.
        var existing = await db.ReceiveConnectors.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is null)
            return;

        db.ReceiveConnectors.Remove(existing);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AdminAuditActions.ReceiveConnectorDeleted, actor,
            detail: $"{existing.Name} {existing.Address}:{existing.Port}", ct: ct);
    }
}

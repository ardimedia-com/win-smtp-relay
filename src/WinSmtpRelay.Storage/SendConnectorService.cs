using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// The cache serves this data on the SMTP hot path; invalidating HERE (not in each caller) means no
// caller — UI page, API endpoint, or background job — can forget to and leave stale policy live for
// up to the cache lifetime. Same convention as IpAccessRuleService.
// Mutations here change where outbound mail goes and with which credentials — audited at the
// SERVICE so no caller can change routing without leaving a trace.
public class SendConnectorService(
    RelayDbContext db,
    IRuntimeConfigCache cache,
    ICurrentActor actor,
    IAdminAuditService audit) : ISendConnectorService
{
    public async Task<IReadOnlyList<SendConnector>> GetAllAsync(CancellationToken ct = default)
    {
        return await db.SendConnectors.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<SendConnector?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await db.SendConnectors.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<SendConnector?> GetDefaultAsync(CancellationToken ct = default)
    {
        return await db.SendConnectors.AsNoTracking().SingleOrDefaultAsync(c => c.IsDefault, ct);
    }

    public async Task<SendConnector> CreateAsync(SendConnector connector, CancellationToken ct = default)
    {
        db.SendConnectors.Add(connector);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.SendConnectorCreated, actor.UserId, actor.Email,
            tenantId: connector.TenantId, detail: $"{connector.Name} -> {connector.SmartHost}", ct: ct);
        return connector;
    }

    public async Task UpdateAsync(SendConnector connector, CancellationToken ct = default)
    {
        var existing = await db.SendConnectors.FirstOrDefaultAsync(c => c.Id == connector.Id, ct);
        if (existing is null) return;

        existing.Name = connector.Name;
        existing.SmartHost = connector.SmartHost;
        existing.SmartHostPort = connector.SmartHostPort;
        existing.Username = connector.Username;
        existing.EncryptedPassword = connector.EncryptedPassword;
        existing.OpportunisticTls = connector.OpportunisticTls;
        existing.RequireTls = connector.RequireTls;
        existing.IsDefault = connector.IsDefault;
        existing.MaxConcurrentDeliveries = connector.MaxConcurrentDeliveries;
        existing.MaxRetryHours = connector.MaxRetryHours;
        existing.RetryIntervalsMinutes = connector.RetryIntervalsMinutes;
        existing.ConnectTimeoutSeconds = connector.ConnectTimeoutSeconds;
        existing.IsEnabled = connector.IsEnabled;

        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.SendConnectorUpdated, actor.UserId, actor.Email,
            tenantId: existing.TenantId, detail: $"{existing.Name} -> {existing.SmartHost}", ct: ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        // Load-then-delete so the audit row can name the connector that was removed.
        var existing = await db.SendConnectors.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is null)
            return;

        db.SendConnectors.Remove(existing);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.SendConnectorDeleted, actor.UserId, actor.Email,
            tenantId: existing.TenantId, detail: $"{existing.Name} -> {existing.SmartHost}", ct: ct);
    }
}

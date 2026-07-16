using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// The cache serves this data on the SMTP hot path; invalidating HERE (not in each caller) means no
// caller — UI page, API endpoint, or background job — can forget to and leave stale policy live for
// up to the cache lifetime. Same convention as IpAccessRuleService.
public class SendConnectorService(RelayDbContext db, IRuntimeConfigCache cache) : ISendConnectorService
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
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await db.SendConnectors.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        cache.Invalidate();
    }
}

using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class DkimDomainService(RelayDbContext db) : IDkimDomainService
{
    public async Task<IReadOnlyList<DkimDomain>> GetAllAsync(CancellationToken ct = default)
    {
        return await db.DkimDomains.AsNoTracking().OrderBy(d => d.Domain).ToListAsync(ct);
    }

    public async Task<DkimDomain?> GetByDomainAsync(string domain, CancellationToken ct = default)
    {
        // FirstOrDefault: a domain is unique only per tenant now (composite index).
        return await db.DkimDomains.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Domain == domain, ct);
    }

    public async Task<DkimDomain?> GetForSigningAsync(int tenantId, string domain, CancellationToken ct = default)
    {
        // Explicit tenant filter so the delivery signer never picks up another tenant's key.
        return await db.DkimDomains.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Domain == domain && d.IsEnabled, ct);
    }

    public async Task<DkimDomain> CreateAsync(DkimDomain dkim, CancellationToken ct = default)
    {
        db.DkimDomains.Add(dkim);
        await db.SaveChangesAsync(ct);
        return dkim;
    }

    public async Task UpdateAsync(DkimDomain dkim, CancellationToken ct = default)
    {
        var existing = await db.DkimDomains.FirstOrDefaultAsync(d => d.Id == dkim.Id, ct);
        if (existing is null) return;

        existing.Domain = dkim.Domain;
        existing.Selector = dkim.Selector;
        existing.PrivateKeyPath = dkim.PrivateKeyPath;
        existing.IsEnabled = dkim.IsEnabled;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await db.DkimDomains.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
    }
}

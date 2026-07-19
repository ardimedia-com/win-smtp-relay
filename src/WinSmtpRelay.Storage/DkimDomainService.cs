using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// Mutations here change how outbound mail is cryptographically signed — audited at the SERVICE so no
// caller can swap a signing key without leaving a trace. Audit details name domain/selector only,
// NEVER the private-key material.
public class DkimDomainService(
    RelayDbContext db,
    ICurrentActor actor,
    IAdminAuditService audit) : IDkimDomainService
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
        await audit.WriteAsync(AdminAuditActions.DkimDomainCreated, actor, tenantId: dkim.TenantId,
            detail: $"{dkim.Selector}._domainkey.{dkim.Domain}", ct: ct);
        return dkim;
    }

    public async Task UpdateAsync(DkimDomain dkim, CancellationToken ct = default)
    {
        var existing = await db.DkimDomains.FirstOrDefaultAsync(d => d.Id == dkim.Id, ct);
        if (existing is null) return;

        existing.Domain = dkim.Domain;
        existing.Selector = dkim.Selector;
        existing.PrivateKeyPath = dkim.PrivateKeyPath;
        existing.PrivateKeyPem = dkim.PrivateKeyPem;
        existing.IsEnabled = dkim.IsEnabled;

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AdminAuditActions.DkimDomainUpdated, actor, tenantId: existing.TenantId,
            detail: $"{existing.Selector}._domainkey.{existing.Domain} enabled={existing.IsEnabled}", ct: ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        // Load-then-delete so the audit row can name the domain whose signing config was removed.
        var existing = await db.DkimDomains.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (existing is null)
            return;

        db.DkimDomains.Remove(existing);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AdminAuditActions.DkimDomainDeleted, actor, tenantId: existing.TenantId,
            detail: $"{existing.Selector}._domainkey.{existing.Domain}", ct: ct);
    }
}

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// The cache serves this data on the SMTP hot path; invalidating HERE (not in each caller) means no
// caller — UI page, API endpoint, or background job — can forget to and leave stale policy live for
// up to the cache lifetime. Same convention as IpAccessRuleService.
// Mutations here change which domains may send/receive — audited at the SERVICE so no caller can
// change policy without leaving a trace (actor from the ambient ICurrentActor; system scopes audit
// with a null actor).
public class AcceptedDomainService(
    RelayDbContext db,
    IRuntimeConfigCache cache,
    ICurrentActor actor,
    IAdminAuditService audit) : IAcceptedDomainService
{
    public async Task<IReadOnlyList<AcceptedDomain>> GetAllAsync(CancellationToken ct = default)
    {
        return await db.AcceptedDomains.AsNoTracking().OrderBy(d => d.Domain).ToListAsync(ct);
    }

    public async Task<AcceptedDomain> CreateAsync(string domain, CancellationToken ct = default)
    {
        var normalized = domain.ToLowerInvariant().Trim();

        // Recipient domains are globally unique — guard across all tenants (the page/API pre-check
        // via ExistsAsync; this is the backstop before the unique index would throw).
        if (await db.AcceptedDomains.IgnoreQueryFilters().AsNoTracking().AnyAsync(d => d.Domain == normalized, ct))
            throw new InvalidOperationException($"Domain '{normalized}' is already in use.");

        var entry = new AcceptedDomain { Domain = normalized, VerificationToken = GenerateToken() };
        db.AcceptedDomains.Add(entry);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.RecipientDomainCreated, actor,
            tenantId: entry.TenantId, detail: entry.Domain, ct: ct);
        return entry;
    }

    public async Task MarkVerifiedAsync(int id, CancellationToken ct = default)
    {
        var entry = await db.AcceptedDomains.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entry is null)
            return;

        entry.VerifiedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.RecipientDomainVerified, actor,
            tenantId: entry.TenantId, detail: entry.Domain, ct: ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        // Load-then-delete so the audit row can name the domain that was removed.
        var entry = await db.AcceptedDomains.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entry is null)
            return;

        db.AcceptedDomains.Remove(entry);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.RecipientDomainDeleted, actor,
            tenantId: entry.TenantId, detail: entry.Domain, ct: ct);
    }

    private static string GenerateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public async Task<bool> ExistsAsync(string domain, CancellationToken ct = default)
    {
        // Global check (ignore the tenant filter): a domain claimed by any tenant counts as taken.
        return await db.AcceptedDomains.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(d => d.Domain == domain.ToLowerInvariant().Trim(), ct);
    }
}

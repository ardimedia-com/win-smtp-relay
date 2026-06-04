using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class AcceptedDomainService(RelayDbContext db) : IAcceptedDomainService
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

        var entry = new AcceptedDomain { Domain = normalized };
        db.AcceptedDomains.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await db.AcceptedDomains.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<bool> ExistsAsync(string domain, CancellationToken ct = default)
    {
        // Global check (ignore the tenant filter): a domain claimed by any tenant counts as taken.
        return await db.AcceptedDomains.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(d => d.Domain == domain.ToLowerInvariant().Trim(), ct);
    }
}

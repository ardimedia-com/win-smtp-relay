using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class AcceptedSenderDomainService(RelayDbContext db) : IAcceptedSenderDomainService
{
    public async Task<IReadOnlyList<AcceptedSenderDomain>> GetAllAsync(CancellationToken ct = default)
    {
        return await db.AcceptedSenderDomains.AsNoTracking().OrderBy(d => d.Domain).ToListAsync(ct);
    }

    public async Task<AcceptedSenderDomain> CreateAsync(string domain, CancellationToken ct = default)
    {
        var normalized = domain.ToLowerInvariant().Trim();

        // Sender domains are globally unique — guard across all tenants (the page/API pre-check
        // via ExistsAsync; this is the backstop before the unique index would throw).
        if (await db.AcceptedSenderDomains.IgnoreQueryFilters().AsNoTracking().AnyAsync(d => d.Domain == normalized, ct))
            throw new InvalidOperationException($"Domain '{normalized}' is already in use.");

        var entry = new AcceptedSenderDomain { Domain = normalized, VerificationToken = GenerateToken() };
        db.AcceptedSenderDomains.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task MarkVerifiedAsync(int id, CancellationToken ct = default)
    {
        var entry = await db.AcceptedSenderDomains.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entry is null)
            return;

        entry.VerifiedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await db.AcceptedSenderDomains.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
    }

    private static string GenerateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public async Task<bool> ExistsAsync(string domain, CancellationToken ct = default)
    {
        // Global check (ignore the tenant filter): a domain claimed by any tenant counts as taken.
        return await db.AcceptedSenderDomains.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(d => d.Domain == domain.ToLowerInvariant().Trim(), ct);
    }
}

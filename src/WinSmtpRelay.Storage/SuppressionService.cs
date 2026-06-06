using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class SuppressionService(RelayDbContext db) : ISuppressionService
{
    public async Task<bool> IsSuppressedAsync(string address, int tenantId, CancellationToken ct = default)
    {
        var normalized = Normalize(address);
        if (normalized.Length == 0)
            return false;

        // Explicit tenant filter (+ IgnoreQueryFilters) so this works from the unscoped delivery worker
        // regardless of the ambient tenant scope.
        return await db.SuppressionEntries
            .IgnoreQueryFilters()
            .AnyAsync(e => e.TenantId == tenantId && e.Address == normalized, ct);
    }

    public async Task AddAsync(string address, SuppressionReason reason, string? detail, int tenantId, CancellationToken ct = default)
    {
        var normalized = Normalize(address);
        if (normalized.Length == 0)
            return;

        var exists = await db.SuppressionEntries
            .IgnoreQueryFilters()
            .AnyAsync(e => e.TenantId == tenantId && e.Address == normalized, ct);
        if (exists)
            return;

        db.SuppressionEntries.Add(new SuppressionEntry
        {
            TenantId = tenantId,
            Address = normalized,
            Reason = reason,
            Detail = Truncate(detail, 500)
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent add for the same (tenant, address) hit the unique index — treat as success.
            db.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyList<SuppressionEntry>> GetAllAsync(CancellationToken ct = default)
        => await db.SuppressionEntries.AsNoTracking().OrderByDescending(e => e.Id).ToListAsync(ct);

    public async Task RemoveAsync(int id, CancellationToken ct = default)
        => await db.SuppressionEntries.Where(e => e.Id == id).ExecuteDeleteAsync(ct);

    private static string Normalize(string address) => address.Trim().ToLowerInvariant();

    private static string? Truncate(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] : s;
}

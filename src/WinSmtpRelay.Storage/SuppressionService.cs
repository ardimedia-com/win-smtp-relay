using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// Suppression changes alter who the relay will deliver to — audited at the SERVICE so both the admin
// surfaces (UI, API) and the automatic sources (hard bounce / complaint, which audit with the honest
// null actor) leave a trace. Volume is low: one row per newly-suppressed address, not per attempt.
public class SuppressionService(
    RelayDbContext db,
    ICurrentActor actor,
    IAdminAuditService audit) : ISuppressionService
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
            // A concurrent add for the same (tenant, address) hit the unique index — treat as success
            // (and skip the audit row: the concurrent writer already recorded the suppression).
            db.ChangeTracker.Clear();
            return;
        }

        await audit.WriteAsync(AdminAuditActions.SuppressionAdded, actor, tenantId: tenantId,
            detail: $"{normalized} ({reason})", ct: ct);
    }

    public async Task<IReadOnlyList<SuppressionEntry>> GetAllAsync(CancellationToken ct = default)
        => await db.SuppressionEntries.AsNoTracking().OrderByDescending(e => e.Id).ToListAsync(ct);

    public async Task RemoveAsync(int id, CancellationToken ct = default)
    {
        // Load-then-delete so the audit row can name the address that becomes deliverable again.
        // Both queries go through the ambient tenant filter, so a tenant-scoped caller can neither
        // see nor remove another tenant's entry.
        var existing = await db.SuppressionEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (existing is null)
            return;

        await db.SuppressionEntries.Where(e => e.Id == id).ExecuteDeleteAsync(ct);
        await audit.WriteAsync(AdminAuditActions.SuppressionRemoved, actor, tenantId: existing.TenantId,
            detail: $"{existing.Address} (was {existing.Reason})", ct: ct);
    }

    private static string Normalize(string address) => address.Trim().ToLowerInvariant();

    private static string? Truncate(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] : s;
}

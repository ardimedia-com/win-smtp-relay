using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

/// <summary>EF-backed <see cref="IAdminMembershipService"/> store. Memberships are NOT
/// tenant-query-filtered, so this is the one place that reads/writes them across scopes intentionally.</summary>
public class AdminMembershipService(RelayDbContext db) : IAdminMembershipService
{
    public async Task<IReadOnlyList<AdminMembership>> GetForUserAsync(int userId, CancellationToken ct = default)
        => await db.AdminMemberships.AsNoTracking().Where(m => m.UserId == userId).ToListAsync(ct);

    public async Task<IReadOnlyList<AdminMembership>> GetForTenantAsync(int tenantId, CancellationToken ct = default)
        => await db.AdminMemberships.AsNoTracking().Where(m => m.TenantId == tenantId).ToListAsync(ct);

    public async Task<IReadOnlyList<AdminMembership>> GetHostAsync(CancellationToken ct = default)
        => await db.AdminMemberships.AsNoTracking().Where(m => m.TenantId == null).ToListAsync(ct);

    public async Task<int> CountHostAdminsAsync(CancellationToken ct = default)
    {
        var hostUserIds = await db.AdminMemberships
            .Where(m => m.TenantId == null).Select(m => m.UserId).Distinct().ToListAsync(ct);
        if (hostUserIds.Count == 0)
            return 0;

        // Filter "enabled" (lockout) in memory: a nullable DateTimeOffset compared with `|| == null`
        // can't be translated under the SQLite ISO-string converter. The host-admin set is small.
        var users = await db.Users.Where(u => hostUserIds.Contains(u.Id)).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return users.Count(u => u.LockoutEnd is null || u.LockoutEnd <= now);
    }

    public async Task<AdminMembership> GrantAsync(int userId, int? tenantId, string role, int? grantedByUserId,
        bool breakGlass = false, CancellationToken ct = default)
    {
        // Idempotent on (user, scope): the unique index can't enforce it for host rows (SQLite treats
        // NULLs as distinct), so enforce it here — update the role of an existing membership instead.
        var existing = await db.AdminMemberships.FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId, ct);
        if (existing is not null)
        {
            var roleChanged = existing.Role != role;
            existing.Role = role;
            if (breakGlass)
                existing.IsBreakGlass = true;
            await db.SaveChangesAsync(ct);
            // A changed role (e.g. admin → viewer) must reach the user's open sessions, whose cookie still
            // carries the old membership claim — rotate the stamp so they are re-issued or signed out.
            if (roleChanged)
                await RotateSecurityStampAsync(userId, ct);
            return existing;
        }

        var membership = new AdminMembership
        {
            UserId = userId,
            TenantId = tenantId,
            Role = role,
            GrantedByUserId = grantedByUserId,
            IsBreakGlass = breakGlass,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        db.AdminMemberships.Add(membership);
        await db.SaveChangesAsync(ct);
        return membership;
    }

    public async Task RevokeAsync(int membershipId, CancellationToken ct = default)
    {
        var membership = await db.AdminMemberships.FirstOrDefaultAsync(m => m.Id == membershipId, ct);
        if (membership is not null)
        {
            db.AdminMemberships.Remove(membership);
            await db.SaveChangesAsync(ct);
            // The revoked membership lives on as a claim in the user's cookie until it is re-validated;
            // rotating the security stamp makes the next validation (cookie and Blazor circuit) end it.
            await RotateSecurityStampAsync(membership.UserId, ct);
        }
    }

    /// <summary>
    /// Rotates the Identity security stamp of one admin account without loading the Identity stack — the value is
    /// opaque to Identity (it only compares it), so any fresh random string invalidates every issued cookie. The
    /// concurrency stamp is deliberately left alone, so a caller holding a tracked copy of the user can still
    /// save it (e.g. re-issue its own cookie with <c>UpdateSecurityStampAsync</c>).
    /// </summary>
    private Task RotateSecurityStampAsync(int userId, CancellationToken ct)
        => db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N").ToUpperInvariant()), ct);
}

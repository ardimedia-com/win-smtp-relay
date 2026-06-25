using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

/// <summary>EF-backed append-only audit writer. Persists immediately so a security event is recorded
/// even when the caller has no other unit of work (e.g. a sign-in).</summary>
public class AdminAuditService(RelayDbContext db) : IAdminAuditService
{
    public async Task WriteAsync(string action, int? actorUserId, string? actorEmail,
        int? targetUserId = null, int? tenantId = null, string? detail = null, CancellationToken ct = default)
    {
        db.AdminAuditEvents.Add(new AdminAuditEvent
        {
            OccurredUtc = DateTimeOffset.UtcNow,
            Action = action,
            ActorUserId = actorUserId,
            ActorEmail = actorEmail,
            TargetUserId = targetUserId,
            TenantId = tenantId,
            Detail = detail is { Length: > 1024 } ? detail[..1024] : detail,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyList<AdminAuditEvent> Events, int Total)> QueryAsync(
        string? action, int? tenantId, string? search, int skip, int take, CancellationToken ct = default)
    {
        var q = db.AdminAuditEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action))
            q = q.Where(e => e.Action == action);
        if (tenantId is int t)
            q = q.Where(e => e.TenantId == t);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(e => (e.ActorEmail != null && e.ActorEmail.Contains(s))
                          || (e.Detail != null && e.Detail.Contains(s)));
        }

        var total = await q.CountAsync(ct);
        // Order by the autoincrement Id (insertion order = chronological), which SQLite always translates.
        var events = await q.OrderByDescending(e => e.Id).Skip(skip).Take(take).ToListAsync(ct);
        return (events, total);
    }
}

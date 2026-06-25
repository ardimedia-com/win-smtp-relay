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
}

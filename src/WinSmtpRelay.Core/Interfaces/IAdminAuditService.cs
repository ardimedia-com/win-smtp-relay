namespace WinSmtpRelay.Core.Interfaces;

/// <summary>Writes append-only admin/security audit events (see <c>AdminAuditEvent</c>).</summary>
public interface IAdminAuditService
{
    Task WriteAsync(string action, int? actorUserId, string? actorEmail,
        int? targetUserId = null, int? tenantId = null, string? detail = null, CancellationToken ct = default);
}

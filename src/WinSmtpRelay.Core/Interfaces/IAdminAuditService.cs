using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>Writes and reads append-only admin/security audit events (see <c>AdminAuditEvent</c>).</summary>
public interface IAdminAuditService
{
    Task WriteAsync(string action, int? actorUserId, string? actorEmail,
        int? targetUserId = null, int? tenantId = null, string? detail = null, CancellationToken ct = default);

    /// <summary>
    /// Returns a page of audit events (newest first) and the total matching count. Optional filters:
    /// exact <paramref name="action"/>, exact <paramref name="tenantId"/>, and a free-text
    /// <paramref name="search"/> over actor email and detail.
    /// </summary>
    Task<(IReadOnlyList<AdminAuditEvent> Events, int Total)> QueryAsync(
        string? action, int? tenantId, string? search, int skip, int take, CancellationToken ct = default);
}

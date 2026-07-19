using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>Writes and reads append-only admin/security audit events (see <c>AdminAuditEvent</c>).</summary>
public interface IAdminAuditService
{
    Task WriteAsync(string action, int? actorUserId, string? actorEmail,
        int? targetUserId = null, int? tenantId = null, string? detail = null,
        int? actorApiKeyId = null, CancellationToken ct = default);

    /// <summary>
    /// Convenience overload taking the ambient actor whole, so a service can never forget one of the
    /// three actor fields (user id, API key id, display name) when auditing its own mutation.
    /// </summary>
    Task WriteAsync(string action, ICurrentActor actor,
        int? targetUserId = null, int? tenantId = null, string? detail = null, CancellationToken ct = default)
        => WriteAsync(action, actor.UserId, actor.Email, targetUserId, tenantId, detail, actor.ApiKeyId, ct);

    /// <summary>
    /// Returns a page of audit events (newest first) and the total matching count. Optional filters:
    /// exact <paramref name="action"/>, exact <paramref name="tenantId"/>, and a free-text
    /// <paramref name="search"/> over actor email and detail.
    /// </summary>
    Task<(IReadOnlyList<AdminAuditEvent> Events, int Total)> QueryAsync(
        string? action, int? tenantId, string? search, int skip, int take, CancellationToken ct = default);
}

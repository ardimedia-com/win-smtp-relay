namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Append-only record of an admin- or security-relevant action: account and membership lifecycle
/// (create/disable/delete, grant/revoke), break-glass tenant access, and sign-in outcomes. Written
/// from Phase 1 on; the in-UI audit view is a later phase. Designed to be queryable by actor, target,
/// tenant and time.
/// </summary>
public class AdminAuditEvent
{
    public int Id { get; set; }

    public DateTimeOffset OccurredUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The admin who performed the action; null for system/anonymous events.</summary>
    public int? ActorUserId { get; set; }

    /// <summary>
    /// The API key that performed the action, when the caller authenticated with one (automation,
    /// MCP) instead of a signed-in admin. Mutually exclusive with <see cref="ActorUserId"/>; both
    /// null = system/background. Denormalised alongside <see cref="ActorEmail"/> (which then carries
    /// the key's name) so the trail survives key deletion.
    /// </summary>
    public int? ActorApiKeyId { get; set; }

    /// <summary>The actor's email at the time (denormalised so the trail survives account deletion).
    /// For an API-key actor this is the key's name.</summary>
    public string? ActorEmail { get; set; }

    /// <summary>A stable action key (see <c>AdminAuditActions</c>).</summary>
    public string Action { get; set; } = "";

    /// <summary>The account the action targeted, if any.</summary>
    public int? TargetUserId { get; set; }

    /// <summary>The tenant the action concerned, if any.</summary>
    public int? TenantId { get; set; }

    /// <summary>Free-form context (role, break-glass reason, …), not parsed.</summary>
    public string? Detail { get; set; }
}

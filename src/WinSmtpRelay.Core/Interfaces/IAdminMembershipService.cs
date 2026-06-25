using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Manages <see cref="AdminMembership"/> rows — the single source of truth for admin access. Grants and
/// revocations are audited by the caller (or by the implementation) so the access history is traceable.
/// </summary>
public interface IAdminMembershipService
{
    /// <summary>All memberships of one user (host and tenant).</summary>
    Task<IReadOnlyList<AdminMembership>> GetForUserAsync(int userId, CancellationToken ct = default);

    /// <summary>All memberships granting access to one tenant.</summary>
    Task<IReadOnlyList<AdminMembership>> GetForTenantAsync(int tenantId, CancellationToken ct = default);

    /// <summary>All host-level memberships (one per host admin).</summary>
    Task<IReadOnlyList<AdminMembership>> GetHostAsync(CancellationToken ct = default);

    /// <summary>Number of enabled accounts holding a host membership — used to guard the last host admin.</summary>
    Task<int> CountHostAdminsAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates (or updates the role of) a membership for <paramref name="userId"/> in the given scope
    /// (<paramref name="tenantId"/> null = host). Idempotent on (user, scope). Returns the membership.
    /// </summary>
    Task<AdminMembership> GrantAsync(int userId, int? tenantId, string role, int? grantedByUserId,
        bool breakGlass = false, CancellationToken ct = default);

    /// <summary>Removes a membership by id. No-op if it does not exist.</summary>
    Task RevokeAsync(int membershipId, CancellationToken ct = default);
}

using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.AdminUi.Services;

/// <summary>Sign-in gate helpers that are now membership-aware (a user may hold several memberships).</summary>
public static class AccountAccess
{
    /// <summary>
    /// True when the account has at least one usable scope: a host membership, or a membership in an
    /// enabled tenant. Replaces the old single-tenant "is your tenant disabled?" sign-in check — a
    /// user whose only access is to disabled tenants (and who is not a host admin) cannot sign in.
    /// </summary>
    public static async Task<bool> HasUsableScopeAsync(
        IAdminMembershipService memberships, IRuntimeConfigCache cache, int userId, CancellationToken ct = default)
    {
        var mems = await memberships.GetForUserAsync(userId, ct);
        if (mems.Any(m => m.TenantId is null))
            return true; // host membership — always usable

        foreach (var m in mems.Where(m => m.TenantId is not null))
            if (await cache.IsTenantEnabledAsync(m.TenantId!.Value, ct))
                return true;

        return false;
    }
}

using System.Security.Claims;
using WinSmtpRelay.Core.Authorization;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// An API key that may manage keys (<c>admin:write</c>) must not mint or re-scope a key with more power than its
/// own: not a higher role, and no scope it does not hold itself. Otherwise a narrowly scoped automation key could
/// create an unrestricted one. Cookie-authenticated admins are governed by their role alone (as everywhere else).
/// </summary>
public static class ApiKeyCeiling
{
    /// <summary>Returns why the requested key exceeds the calling key, or null when it is within it (or the caller is not a key).</summary>
    public static string? Check(ClaimsPrincipal caller, string requestedRole, string? requestedScopes)
    {
        if (caller.FindFirst(RelayClaimTypes.ApiKeyId) is null)
            return null;

        if (Rank(requestedRole) > CallerRank(caller))
            return $"This API key cannot create or change a key with the role '{requestedRole}', which is above its own.";

        var own = ApiKeyScopes.Parse(caller.FindFirst(RelayClaimTypes.ApiKeyScopes)?.Value);
        var requested = ApiKeyScopes.Parse(requestedScopes);

        // A scope-less key reads every area: only a caller that may read every area can hand that out.
        if (requested.Count == 0)
        {
            var unreadable = ApiKeyScopes.Areas.Where(a => !ApiKeyScopes.AllowsRead(own, a)).ToList();
            return unreadable.Count == 0
                ? null
                : $"A key without scopes may read every area; this API key cannot read {string.Join(", ", unreadable)} itself. Name the scopes explicitly.";
        }

        foreach (var scope in requested)
        {
            var allowed = scope == ApiKeyScopes.MessagesBody
                ? ApiKeyScopes.AllowsBody(own)
                : scope.EndsWith(":write", StringComparison.Ordinal)
                    ? ApiKeyScopes.AllowsWrite(own, scope[..^":write".Length])
                    : ApiKeyScopes.AllowsRead(own, scope[..^":read".Length]);
            if (!allowed)
                return $"This API key does not hold the scope '{scope}' itself, so it cannot grant it.";
        }
        return null;
    }

    private static int CallerRank(ClaimsPrincipal caller)
    {
        if (RelayAccess.HasHostMembership(caller))
            return Rank(RelayRoles.HostAdmin);
        var roles = RelayAccess.TenantMemberships(caller).Values.ToList();
        return roles.Count == 0 ? 0 : roles.Max(Rank);
    }

    private static int Rank(string role) => role switch
    {
        RelayRoles.HostAdmin => 3,
        RelayRoles.TenantAdmin => 2,
        RelayRoles.TenantViewer => 1,
        _ => int.MaxValue,
    };
}

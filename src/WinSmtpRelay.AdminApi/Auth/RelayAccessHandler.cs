using Microsoft.AspNetCore.Authorization;
using WinSmtpRelay.Core.Authorization;

namespace WinSmtpRelay.AdminApi.Auth;

public enum RelayAccessLevel
{
    /// <summary>Host-level administration (tenant lifecycle, server config, host admins).</summary>
    Host,
    /// <summary>Read/write within the active tenant.</summary>
    Full,
    /// <summary>Read within the active tenant.</summary>
    View,
}

/// <summary>Requirement carrying the access level a policy demands; evaluated by <see cref="RelayAccessHandler"/>.</summary>
public sealed class RelayAccessRequirement(RelayAccessLevel level) : IAuthorizationRequirement
{
    public RelayAccessLevel Level { get; } = level;
}

/// <summary>
/// Evaluates the consent-based, membership-driven access rule (see <see cref="RelayAccess"/>) for the
/// three relay policies. Reads only the principal's claims, so it is independent of request/circuit
/// timing and works uniformly for cookie and API-key callers.
/// </summary>
public sealed class RelayAccessHandler : AuthorizationHandler<RelayAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RelayAccessRequirement requirement)
    {
        var user = context.User;
        var granted = requirement.Level switch
        {
            RelayAccessLevel.Host => RelayAccess.CanHostAdmin(user),
            RelayAccessLevel.Full => RelayAccess.CanFull(user),
            RelayAccessLevel.View => RelayAccess.CanView(user),
            _ => false,
        };
        if (granted)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

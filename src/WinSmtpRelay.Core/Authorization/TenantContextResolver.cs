using System.Security.Claims;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Core.Authorization;

/// <summary>
/// Maps an authenticated principal's claims onto the ambient <see cref="ICurrentTenant"/>:
/// host admins default to all-tenants but honor their <see cref="RelayClaimTypes.ActiveTenant"/>
/// switcher selection; tenant principals are scoped to their tenant; an authenticated principal
/// with neither claim is scoped to a non-existent tenant (sees nothing).
/// </summary>
public static class TenantContextResolver
{
    public static void Apply(ClaimsPrincipal user, ICurrentTenant current)
    {
        if (user.Identity?.IsAuthenticated != true)
            return;

        if (user.HasClaim(RelayClaimTypes.IsHostAdmin, "true"))
        {
            if (int.TryParse(user.FindFirst(RelayClaimTypes.ActiveTenant)?.Value, out var activeTenant))
                current.SetTenant(activeTenant);
            else
                current.SetHostScope();
        }
        else if (int.TryParse(user.FindFirst(RelayClaimTypes.TenantId)?.Value, out var tenantId))
        {
            current.SetTenant(tenantId);
        }
        else
        {
            current.SetTenant(-1);
        }
    }
}

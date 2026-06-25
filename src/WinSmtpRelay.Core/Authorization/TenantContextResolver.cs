using System.Security.Claims;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Core.Authorization;

/// <summary>
/// Maps an authenticated principal's membership claims onto the ambient <see cref="ICurrentTenant"/>
/// (the EF tenant filter). A host admin with no active tenant is host scope (all tenants); otherwise
/// the principal is scoped to the active/sole tenant. An authenticated principal with no usable scope
/// is scoped to a non-existent tenant (sees nothing). Note: the EF scope only governs which data is
/// visible — access to a tenant's pages is gated separately by <see cref="RelayAccess"/>, which a host
/// membership alone does not satisfy.
/// </summary>
public static class TenantContextResolver
{
    public static void Apply(ClaimsPrincipal user, ICurrentTenant current)
    {
        if (user.Identity?.IsAuthenticated != true)
            return;

        var scopeTenant = RelayAccess.CurrentScopeTenant(user);
        if (scopeTenant is int tenantId)
            current.SetTenant(tenantId);
        else if (RelayAccess.HasHostMembership(user))
            current.SetHostScope();
        else
            current.SetTenant(-1);
    }
}

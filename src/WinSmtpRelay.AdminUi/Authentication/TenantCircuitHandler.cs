using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.AdminUi.Authentication;

/// <summary>
/// Establishes the ambient tenant for a Blazor circuit from the authenticated user, so that
/// tenant-scoped query filtering applies to interactive admin pages (the HTTP request-scoped
/// middleware only covers the initial prerender, not the live circuit).
/// </summary>
public class TenantCircuitHandler(AuthenticationStateProvider authStateProvider, ICurrentTenant currentTenant)
    : CircuitHandler
{
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var state = await authStateProvider.GetAuthenticationStateAsync();
        var user = state.User;
        if (user.Identity?.IsAuthenticated != true)
            return;

        if (user.HasClaim(RelayClaimTypes.IsHostAdmin, "true"))
            currentTenant.SetHostScope();
        else if (int.TryParse(user.FindFirst(RelayClaimTypes.TenantId)?.Value, out var tenantId))
            currentTenant.SetTenant(tenantId);
        else
            currentTenant.SetTenant(-1);
    }
}

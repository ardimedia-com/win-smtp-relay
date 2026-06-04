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
        TenantContextResolver.Apply(state.User, currentTenant);
    }
}

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;

namespace WinSmtpRelay.AdminUi.Authentication;

public static class RelayAdminUiExtensions
{
    /// <summary>
    /// Registers the Blazor-side authentication state plumbing (cascading auth state +
    /// security-stamp revalidation) and the per-circuit tenant context. Call alongside
    /// AddRelayAdminAuth.
    /// </summary>
    public static IServiceCollection AddRelayAdminUiAuth(this IServiceCollection services)
    {
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider>();
        services.AddScoped<CircuitHandler, TenantCircuitHandler>();
        services.AddSingleton<Services.ISignupRateLimiter, Services.SignupRateLimiter>();
        return services;
    }
}

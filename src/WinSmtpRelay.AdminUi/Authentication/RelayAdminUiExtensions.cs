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
        // Per-circuit browser time zone, so UTC timestamps render in the viewer's local time.
        services.AddScoped<Services.BrowserTimeService>();
        // Proposes the relay's public IP (via an external lookup) when configuring Sending IPs.
        services.AddScoped<Services.PublicIpDetector>();
        // Lists the host's local IPv4s and tests outbound SMTP from a chosen source IP (egress-IP UI).
        services.AddSingleton<Services.LocalNetworkProbe>();
        return services;
    }
}

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace WinSmtpRelay.AdminUi.Authentication;

public static class RelayAdminUiExtensions
{
    /// <summary>
    /// Registers the Blazor-side authentication state plumbing (cascading auth state +
    /// security-stamp revalidation). Call alongside AddRelayAdminAuth.
    /// </summary>
    public static IServiceCollection AddRelayAdminUiAuth(this IServiceCollection services)
    {
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider>();
        return services;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.AdminUi.Authentication;

/// <summary>
/// Periodically revalidates the signed-in admin against the security stamp, so a
/// disabled/deleted account or a password change invalidates active circuits.
/// </summary>
public class RevalidatingIdentityAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> optionsAccessor)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    private readonly IdentityOptions _options = optionsAccessor.Value;

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        return await ValidateSecurityStampAsync(userManager, authenticationState.User);
    }

    private async Task<bool> ValidateSecurityStampAsync(UserManager<AdminUser> userManager, ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
            return false;
        if (!userManager.SupportsUserSecurityStamp)
            return true;

        var principalStamp = principal.FindFirstValue(_options.ClaimsIdentity.SecurityStampClaimType);
        var userStamp = await userManager.GetSecurityStampAsync(user);
        return principalStamp == userStamp;
    }
}

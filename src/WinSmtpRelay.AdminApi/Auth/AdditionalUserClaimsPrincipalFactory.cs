using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// Adds tenant/host claims to the cookie principal so the same claim set is present
/// regardless of whether the caller authenticated via cookie or API key.
/// </summary>
public class AdditionalUserClaimsPrincipalFactory(
    UserManager<AdminUser> userManager,
    RoleManager<AdminRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AdminUser, AdminRole>(userManager, roleManager, options)
{
    public override async Task<ClaimsPrincipal> CreateAsync(AdminUser user)
    {
        var principal = await base.CreateAsync(user);
        if (principal.Identity is not ClaimsIdentity identity)
            return principal;

        if (user.TenantId is not null)
            identity.AddClaim(new Claim(RelayClaimTypes.TenantId, user.TenantId.Value.ToString()));
        if (user.IsHostAdmin)
            identity.AddClaim(new Claim(RelayClaimTypes.IsHostAdmin, "true"));
        if (user.MustChangePassword)
            identity.AddClaim(new Claim(RelayClaimTypes.MustChangePassword, "true"));

        return principal;
    }
}

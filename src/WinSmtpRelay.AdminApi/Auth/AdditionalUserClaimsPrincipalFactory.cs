using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// Adds tenant/host claims to the cookie principal so the same claim set is present
/// regardless of whether the caller authenticated via cookie or API key.
/// </summary>
public class AdditionalUserClaimsPrincipalFactory(
    UserManager<AdminUser> userManager,
    RoleManager<AdminRole> roleManager,
    IOptions<IdentityOptions> options,
    ITenantService tenantService)
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
        {
            identity.AddClaim(new Claim(RelayClaimTypes.IsHostAdmin, "true"));
            // Single-tenant deployments have no use for the host/tenant scope split. Scope the host admin
            // to the sole tenant so the merged (no-switcher) view shows the tenant pages too. With more
            // than one tenant, leave it unset (all-tenants/host scope) so the switcher governs the scope.
            var tenants = await tenantService.GetAllAsync();
            if (tenants.Count == 1)
                identity.AddClaim(new Claim(RelayClaimTypes.ActiveTenant, tenants[0].Id.ToString()));
        }
        if (user.MustChangePassword)
            identity.AddClaim(new Claim(RelayClaimTypes.MustChangePassword, "true"));

        return principal;
    }
}

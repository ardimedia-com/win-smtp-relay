using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// Adds membership claims to the cookie principal so authorization (see <see cref="RelayAccess"/>) and
/// tenant scoping work from the cookie without a per-request DB hit, and identically to the API-key path.
/// A host membership emits <see cref="RelayClaimTypes.IsHostAdmin"/>; each tenant membership emits a
/// <see cref="RelayClaimTypes.TenantMembership"/> claim. The active scope is seeded here (single-tenant
/// auto-scope for host admins; the sole/first tenant for tenant users).
/// </summary>
public class AdditionalUserClaimsPrincipalFactory(
    UserManager<AdminUser> userManager,
    RoleManager<AdminRole> roleManager,
    IOptions<IdentityOptions> options,
    ITenantService tenantService,
    IAdminMembershipService memberships)
    : UserClaimsPrincipalFactory<AdminUser, AdminRole>(userManager, roleManager, options)
{
    public override async Task<ClaimsPrincipal> CreateAsync(AdminUser user)
    {
        var principal = await base.CreateAsync(user);
        if (principal.Identity is not ClaimsIdentity identity)
            return principal;

        var userMemberships = await memberships.GetForUserAsync(user.Id);
        var hasHost = userMemberships.Any(m => m.TenantId is null);
        var tenantMemberships = userMemberships.Where(m => m.TenantId is not null).ToList();

        if (hasHost)
            identity.AddClaim(new Claim(RelayClaimTypes.IsHostAdmin, "true"));
        foreach (var m in tenantMemberships)
            identity.AddClaim(new Claim(RelayClaimTypes.TenantMembership, $"{m.TenantId}:{m.Role}"));

        // Seed the active scope:
        //  - host admin in a single-tenant deployment → auto-scope to that sole tenant (merged view,
        //    no switcher); with more than one tenant leave it unset (host/all-tenants scope).
        //  - a tenant user → land in their lowest tenant membership (the switcher governs from there).
        if (hasHost)
        {
            var tenants = await tenantService.GetAllAsync();
            if (tenants.Count == 1)
                identity.AddClaim(new Claim(RelayClaimTypes.ActiveTenant, tenants[0].Id.ToString()));
        }
        else if (tenantMemberships.Count > 0)
        {
            var landing = tenantMemberships.Min(m => m.TenantId!.Value);
            identity.AddClaim(new Claim(RelayClaimTypes.ActiveTenant, landing.ToString()));
        }

        if (user.MustChangePassword)
            identity.AddClaim(new Claim(RelayClaimTypes.MustChangePassword, "true"));

        return principal;
    }
}

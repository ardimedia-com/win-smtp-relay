using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.AdminApi.Auth;

public static class RelayTenantContextMiddleware
{
    /// <summary>
    /// Sets the ambient <see cref="ICurrentTenant"/> from the authenticated principal's claims.
    /// Must run after authentication/authorization. Host admins get an all-tenants scope; a
    /// principal with a tenant claim is scoped to that tenant; an authenticated principal with
    /// neither is scoped to a non-existent tenant (sees nothing) as a safe default.
    /// </summary>
    public static IApplicationBuilder UseRelayTenantContext(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            var user = ctx.User;
            if (user.Identity?.IsAuthenticated == true)
            {
                var current = ctx.RequestServices.GetRequiredService<ICurrentTenant>();
                if (user.HasClaim(RelayClaimTypes.IsHostAdmin, "true"))
                    current.SetHostScope();
                else if (int.TryParse(user.FindFirst(RelayClaimTypes.TenantId)?.Value, out var tenantId))
                    current.SetTenant(tenantId);
                else
                    current.SetTenant(-1);
            }

            await next();
        });
}

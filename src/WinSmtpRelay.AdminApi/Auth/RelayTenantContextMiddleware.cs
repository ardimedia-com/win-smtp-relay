using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.AdminApi.Auth;

public static class RelayTenantContextMiddleware
{
    /// <summary>
    /// Request header with which an API-key caller names the tenant a request acts in (id or slug).
    /// Applies to /api and /mcp alike; see <see cref="TenantSelector"/>.
    /// </summary>
    public const string TenantHeader = "X-Relay-Tenant";

    /// <summary>
    /// Sets the ambient <see cref="ICurrentTenant"/> and <see cref="ICurrentActor"/> from the
    /// authenticated principal's claims. Must run after authentication/authorization. Host admins get
    /// an all-tenants scope; a principal with a tenant claim is scoped to that tenant; an authenticated
    /// principal with neither is scoped to a non-existent tenant (sees nothing) as a safe default.
    /// An API-key caller may narrow a host scope to one tenant with the <see cref="TenantHeader"/> header.
    /// </summary>
    public static IApplicationBuilder UseRelayTenantContext(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                var current = ctx.RequestServices.GetRequiredService<ICurrentTenant>();
                TenantContextResolver.Apply(ctx.User, current);
                ActorContextResolver.Apply(ctx.User, ctx.RequestServices.GetRequiredService<ICurrentActor>());

                var requested = ctx.Request.Headers[TenantHeader].ToString();
                if (requested.Length > 0 && ctx.User.HasClaim(c => c.Type == RelayClaimTypes.ApiKeyId))
                {
                    var error = await TenantSelector.ApplyAsync(
                        current, ctx.RequestServices.GetRequiredService<RelayDbContext>(), requested, ctx.RequestAborted);
                    if (error is not null)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await ctx.Response.WriteAsJsonAsync(new { Error = error });
                        return;
                    }
                }
            }

            await next();
        });
}

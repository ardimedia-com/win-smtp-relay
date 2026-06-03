using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.AdminApi.Auth;

public static class RelayAuthExtensions
{
    /// <summary>
    /// Registers admin authentication (ASP.NET Core Identity cookie + API-key scheme via a
    /// smart policy scheme) and the tenant-aware authorization policies.
    /// </summary>
    public static IServiceCollection AddRelayAdminAuth(this IServiceCollection services)
    {
        services.AddIdentityCore<AdminUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireDigit = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<AdminRole>()
            .AddEntityFrameworkStores<RelayDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddScoped<IUserClaimsPrincipalFactory<AdminUser>, AdditionalUserClaimsPrincipalFactory>();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = RelayAuthSchemes.Smart;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            })
            .AddPolicyScheme(RelayAuthSchemes.Smart, "Cookie or API key", options =>
            {
                options.ForwardDefaultSelector = ctx =>
                {
                    if (ctx.Request.Headers.ContainsKey(ApiKeyDefaults.HeaderName))
                        return ApiKeyDefaults.Scheme;
                    var auth = ctx.Request.Headers.Authorization.ToString();
                    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        return ApiKeyDefaults.Scheme;
                    return IdentityConstants.ApplicationScheme;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null)
            .AddIdentityCookies();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "WinSmtpRelay.Admin";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/account/login";
            options.LogoutPath = "/account/logout";
            options.AccessDeniedPath = "/account/access-denied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;

            // API clients get status codes, browsers get redirects.
            options.Events.OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.HostAdmin, p => p.RequireRole(RelayRoles.HostAdmin))
            .AddPolicy(AuthorizationPolicies.AdminFull, p => p.RequireRole(RelayRoles.HostAdmin, RelayRoles.TenantAdmin))
            .AddPolicy(AuthorizationPolicies.AdminView, p => p.RequireRole(RelayRoles.HostAdmin, RelayRoles.TenantAdmin, RelayRoles.TenantViewer));

        return services;
    }
}

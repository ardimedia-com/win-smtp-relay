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
                // Schema v3 adds the AspNetUserPasskeys table (WebAuthn passkey credentials).
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            })
            .AddRoles<AdminRole>()
            .AddEntityFrameworkStores<RelayDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            // Short-lived, single-use tokens for passwordless sign-in links (see MagicLinkTokenProvider).
            .AddTokenProvider<MagicLinkTokenProvider>(WinSmtpRelay.Core.Authorization.MagicLinkDefaults.ProviderName);

        services.AddScoped<IUserClaimsPrincipalFactory<AdminUser>, AdditionalUserClaimsPrincipalFactory>();
        services.AddScoped<WinSmtpRelay.Storage.ITenantSignupService, WinSmtpRelay.Storage.TenantSignupService>();

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
            // Secure on HTTPS; plain HTTP is only ever permitted on loopback (startup refuses non-loopback
            // HTTP, see Program.cs), so the full-authority admin cookie is never sent cleartext over the
            // network. SameAsRequest (not Always) keeps loopback-HTTP local/dev sign-in working.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/account/login";
            options.LogoutPath = "/account/logout";
            options.AccessDeniedPath = "/account/access-denied";
            // 7-day ticket so an admin who leaves the console idle (or whose machine sleeps) stays
            // signed in instead of being bounced to the login page. Sliding renews it on real HTTP
            // requests; note that in-app Blazor Server interaction runs over the SignalR circuit and
            // does NOT issue cookie-bearing requests, so this is effectively the absolute lifetime
            // from sign-in. The cookie is HttpOnly + SameSite=Strict and only travels over HTTPS
            // (non-loopback HTTP is refused at startup), so the longer window is a safe trade-off.
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
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

        // Authorization is membership/scope-aware (consent model), evaluated by RelayAccessHandler from
        // the principal's membership claims — not global Identity roles. The three policies map to the
        // three access levels; every gated page/endpoint already uses one of them, so the new rule
        // applies everywhere without touching individual pages.
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, RelayAccessHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.HostAdmin, p => p.AddRequirements(new RelayAccessRequirement(RelayAccessLevel.Host)))
            .AddPolicy(AuthorizationPolicies.AdminFull, p => p.AddRequirements(new RelayAccessRequirement(RelayAccessLevel.Full)))
            .AddPolicy(AuthorizationPolicies.AdminView, p => p.AddRequirements(new RelayAccessRequirement(RelayAccessLevel.View)));

        return services;
    }
}

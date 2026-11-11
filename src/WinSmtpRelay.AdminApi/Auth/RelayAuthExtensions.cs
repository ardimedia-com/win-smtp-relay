using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.AdminApi.Auth;

public static class RelayAuthExtensions
{
    /// <summary>
    /// How often a signed-in admin's cookie is re-checked against the stored security stamp. Disabling an
    /// account, revoking a membership or resetting a password rotates the stamp, so this is the longest a
    /// revoked session keeps working (the Blazor circuit re-checks on the same interval).
    /// </summary>
    public static readonly TimeSpan SecurityStampValidationInterval = TimeSpan.FromMinutes(1);

    /// <summary>Lifetime of the "don't ask again on this device" two-factor cookie; the 2FA page shows this value.</summary>
    public static readonly TimeSpan TwoFactorRememberLifetime = TimeSpan.FromDays(30);

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
            // Registered as SignInManager<AdminUser> too, so every page keeps injecting the framework type.
            .AddSignInManager<RelaySignInManager>()
            .AddDefaultTokenProviders()
            // Short-lived, single-use tokens for passwordless sign-in links (see MagicLinkTokenProvider).
            .AddTokenProvider<MagicLinkTokenProvider>(WinSmtpRelay.Core.Authorization.MagicLinkDefaults.ProviderName);

        services.AddScoped<IUserClaimsPrincipalFactory<AdminUser>, AdditionalUserClaimsPrincipalFactory>();
        services.AddScoped<WinSmtpRelay.Storage.ITenantSignupService, WinSmtpRelay.Storage.TenantSignupService>();

        // Account lifecycle (disable/enable/unlock/reset with session + audit consequences), first-run setup with
        // its one-time token. (The consent + audit gate in front of message bodies, IMessageBodyAccess, is registered
        // by AddRelayStorage, next to the resend service that depends on it.)
        services.AddScoped<IAdminAccountService, AdminAccountService>();
        services.AddScoped<IInitialAdminSetup, InitialAdminSetup>();
        services.TryAddSingleton(new InitialSetupToken());
        services.TryAddSingleton(sp => new ApiKeyFailureThrottle(sp.GetService<TimeProvider>() ?? TimeProvider.System));

        // Re-check the cookie against the security stamp every minute instead of every 30 (the default), so a
        // disabled admin or a revoked membership ends the session promptly. A valid cookie is re-issued from the
        // claims factory on each check; carry the admin's tenant-switcher choice over, as far as the refreshed
        // memberships still allow it.
        services.Configure<SecurityStampValidatorOptions>(o =>
        {
            o.ValidationInterval = SecurityStampValidationInterval;
            o.OnRefreshingPrincipal = ctx =>
            {
                if (ctx.CurrentPrincipal is { } current && ctx.NewPrincipal?.Identity is ClaimsIdentity refreshed)
                    ActiveTenantCarryOver.Apply(current, refreshed);
                return Task.CompletedTask;
            };
        });

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

        // "Don't ask again on this device": an explicit lifetime the 2FA page can state (the framework default
        // is 14 days, which the page's former "30 days" did not match).
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorRememberMeScheme, o =>
        {
            o.ExpireTimeSpan = TwoFactorRememberLifetime;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

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

            // API clients get status codes, browsers get redirects. /mcp is a protocol endpoint like
            // /api — an MCP client can do nothing with a login-page redirect.
            options.Events.OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/mcp"))
                {
                    // A source refused by the API-key failure throttle gets 429, everything else 401.
                    if (ctx.HttpContext.Items.ContainsKey(ApiKeyFailureThrottle.BlockedItemKey))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        ctx.Response.Headers.RetryAfter = ((int)ApiKeyFailureThrottle.Window.TotalSeconds).ToString();
                        return Task.CompletedTask;
                    }
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/mcp"))
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

    /// <summary>
    /// IP-partitioned throttle for the sign-in surface (policy <see cref="RelayRateLimitPolicies.SignIn"/>): at most
    /// <paramref name="postsPerIpPerMinute"/> form posts per client address and minute across login, sign-in link,
    /// forgot/reset password, two-factor, recovery code, passkey sign-in and first-run setup; page views (GET) are
    /// never limited. It sits in front of the per-account lockout so guessing - and deliberately locking out a
    /// known account - is slowed per source. 0 disables it. Requires <c>app.UseRateLimiter()</c>.
    /// </summary>
    public static IServiceCollection AddRelaySignInRateLimiting(this IServiceCollection services, int postsPerIpPerMinute)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
            options.AddPolicy(RelayRateLimitPolicies.SignIn, http =>
            {
                if (postsPerIpPerMinute <= 0 || HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                    return RateLimitPartition.GetNoLimiter("unlimited");
                var ip = http.Connection.RemoteIpAddress;
                var key = ip is null ? "unknown" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = postsPerIpPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
        return services;
    }
}

/// <summary>Rate-limiter policy names of the admin plane.</summary>
public static class RelayRateLimitPolicies
{
    /// <summary>The sign-in surface; see <see cref="RelayAuthExtensions.AddRelaySignInRateLimiting"/>.</summary>
    public const string SignIn = "relay-signin";
}

/// <summary>
/// Keeps an admin's tenant-switcher choice when the cookie is re-issued from the claims factory (security-stamp
/// refresh): the factory seeds a default scope, which would otherwise reset the switcher on every validation.
/// </summary>
public static class ActiveTenantCarryOver
{
    public static void Apply(ClaimsPrincipal current, ClaimsIdentity refreshed)
    {
        var refreshedPrincipal = new ClaimsPrincipal(refreshed);
        var chosen = RelayAccess.ActiveTenant(current);
        if (chosen is int tenantId)
        {
            // Still a member of the chosen tenant: keep it; otherwise the factory default stands.
            if (!RelayAccess.TenantMemberships(refreshedPrincipal).ContainsKey(tenantId))
                return;
            SetActive(refreshed, tenantId.ToString());
        }
        else if (RelayAccess.HasHostMembership(current) && RelayAccess.HasHostMembership(refreshedPrincipal))
        {
            // The host admin chose host scope ("all tenants"): keep it, even where the factory auto-scopes.
            SetActive(refreshed, null);
        }
    }

    private static void SetActive(ClaimsIdentity identity, string? value)
    {
        foreach (var claim in identity.FindAll(RelayClaimTypes.ActiveTenant).ToList())
            identity.RemoveClaim(claim);
        if (value is not null)
            identity.AddClaim(new Claim(RelayClaimTypes.ActiveTenant, value));
    }
}

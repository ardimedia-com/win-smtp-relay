using System.Security.Claims;
using BlazorBlueprint.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.AdminApi;
using WinSmtpRelay.AdminApi.Auth;
using WinSmtpRelay.AdminUi.Authentication;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Delivery;
using WinSmtpRelay.Security;
using WinSmtpRelay.Service.HealthChecks;
using WinSmtpRelay.SmtpListener;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

// Windows Services run from System32 — set working directory to exe location
// so relative paths (SQLite DB, config files) resolve correctly
Directory.SetCurrentDirectory(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "WinSmtpRelay";
});

// Configuration
builder.Services.Configure<SmtpListenerOptions>(builder.Configuration.GetSection(SmtpListenerOptions.SectionName));
builder.Services.Configure<DeliveryOptions>(builder.Configuration.GetSection(DeliveryOptions.SectionName));
builder.Services.Configure<TlsOptions>(builder.Configuration.GetSection(TlsOptions.SectionName));
builder.Services.Configure<DkimOptions>(builder.Configuration.GetSection(DkimOptions.SectionName));
builder.Services.Configure<AdminUiOptions>(builder.Configuration.GetSection(AdminUiOptions.SectionName));
builder.Services.Configure<EmailAuthenticationOptions>(builder.Configuration.GetSection(EmailAuthenticationOptions.SectionName));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.SectionName));
builder.Services.Configure<MessageFilterOptions>(builder.Configuration.GetSection(MessageFilterOptions.SectionName));
builder.Services.Configure<BackupMxOptions>(builder.Configuration.GetSection(BackupMxOptions.SectionName));
builder.Services.Configure<StatisticsOptions>(builder.Configuration.GetSection(StatisticsOptions.SectionName));
builder.Services.Configure<DataRetentionOptions>(builder.Configuration.GetSection(DataRetentionOptions.SectionName));
builder.Services.Configure<DnsOptions>(builder.Configuration.GetSection(DnsOptions.SectionName));
builder.Services.Configure<HealthCheckOptions>(builder.Configuration.GetSection(HealthCheckOptions.SectionName));
builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.SectionName));

// Storage
var connectionString = builder.Configuration.GetConnectionString("RelayDb") ?? "Data Source=winsmtprelay.db";
builder.Services.AddRelayStorage(connectionString);

// Security engine: SPF/DKIM/DMARC, deliverability DNS checks, DKIM signing, the local auth self-test,
// and the public-suffix/DNS resolvers. The SMTP listener and delivery engine also pull this in, but it
// is composed explicitly here so the engine layering is visible and host-independent.
builder.Services.AddRelaySecurity();

// SMTP Listener
builder.Services.AddSmtpListener();

// Delivery Engine
builder.Services.AddDeliveryEngine();

// Service state reporter — writes registry flag for MSI upgrade detection
builder.Services.AddHostedService<WinSmtpRelay.Service.ServiceStateReporter>();

// Kestrel for Admin UI + API
var adminUiConfig = builder.Configuration.GetSection(AdminUiOptions.SectionName).Get<AdminUiOptions>() ?? new();

var bindsToLoopback = System.Net.IPAddress.TryParse(adminUiConfig.BindAddress, out var adminBindIp)
    && System.Net.IPAddress.IsLoopback(adminBindIp);

// Development convenience: in the Development environment with a loopback bind, ALSO expose a plain-HTTP
// endpoint on the next port (Port+1, 127.0.0.1 only) alongside HTTPS. Browsers disable the password
// manager (autofill/save) on the self-signed-HTTPS warning page, so this gives a localhost HTTP URL where
// it works — without losing HTTPS. Strictly local + dev-only: the HTTP endpoint is hardcoded to loopback
// and never added in Production (an installed service runs Production via --environment Production), so
// production and network access stay HTTPS only.
var devHttpLoopback = builder.Environment.IsDevelopment() && bindsToLoopback;

// Resolve the admin HTTPS certificate up front: a configured PFX if set, otherwise a persistent
// self-signed certificate generated next to the service binaries. This keeps the admin plane on HTTPS
// out of the box — a self-signed cert just means a one-time browser warning until a trusted certificate
// is imported via the admin UI.
// A shared provider holds the certificate Kestrel serves. It starts as the configured PFX or the
// generated self-signed cert; an operator can later import a trusted certificate via the admin UI, which
// swaps it at runtime (Kestrel reads the provider per TLS handshake, so no restart is needed).
var adminCertProvider = new WinSmtpRelay.Core.AdminCertificateProvider();
if (adminUiConfig.Enabled && adminUiConfig.UseHttps)
{
    // Log certificate resolution to the Windows Event Log (and console) so a failure is diagnosable on a
    // headless service host — this runs before the host (and its logging) is built.
    using var certLoggerFactory = LoggerFactory.Create(b =>
    {
        b.AddConsole();
        if (OperatingSystem.IsWindows())
            b.AddEventLog(s => s.SourceName = "WinSmtpRelay.Service");
    });
    var initialCert = WinSmtpRelay.Security.AdminUiCertificate.Resolve(
        adminUiConfig, AppContext.BaseDirectory,
        certLoggerFactory.CreateLogger("WinSmtpRelay.AdminUiCertificate"));
    if (initialCert is not null)
        adminCertProvider.Set(initialCert);
}
builder.Services.AddSingleton<WinSmtpRelay.Core.Interfaces.IAdminCertificateProvider>(adminCertProvider);

// HTTPS is active only when UseHttps is on and a certificate is actually available.
var adminHttpsActive = adminUiConfig.UseHttps && adminCertProvider.Current is not null;

if (adminUiConfig.Enabled)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(System.Net.IPAddress.Parse(adminUiConfig.BindAddress), adminUiConfig.Port, listen =>
        {
            // Configure HTTPS only when a certificate is available. If none could be prepared we deliberately
            // do NOT fall back to the ASP.NET Core dev certificate (absent on a server → startup crash):
            // the service serves HTTP and the loopback guard below refuses that on a network address.
            if (adminUiConfig.UseHttps && adminCertProvider.Current is not null)
                // Read the certificate per handshake so an admin-UI import takes effect without a restart.
                listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => adminCertProvider.Current);
        });

        // Development: an extra plain-HTTP endpoint on loopback (next port) so the browser password manager
        // works locally, without giving up HTTPS. Loopback-only and dev-only — never on the network or in
        // Production.
        if (devHttpLoopback)
            options.Listen(System.Net.IPAddress.Loopback, adminUiConfig.Port + 1);
    });

    // Admin authentication/authorization (cookie + API key) and tenant-aware policies.
    builder.Services.AddRelayAdminAuth();
    builder.Services.AddRelayAdminUiAuth();

    // (Deliverability DNS checks, the local auth self-test, public-suffix lookup, and the public DNS
    // resolver are registered by AddRelaySecurity above — engine services, not UI-gated.)
    // The admin-UI certificate applier is a host concern (it depends on the host-provided
    // IAdminCertificateProvider registered above), so it stays here rather than in the security engine.
    builder.Services.AddScoped<AdminCertificateApplier>();

    // OpenAPI document for the REST API (served at /openapi/v1.json by MapOpenApi below). Lets external
    // clients/tools discover the AdminApi surface; the endpoints themselves still require an API key.
    builder.Services.AddOpenApi();

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents(options =>
        {
            // Keep a disconnected circuit (idle tab, brief network drop, machine sleep) alive far longer
            // than the 3-minute default so returning to the console resumes the same session instead of
            // forcing a full page reload. The auth cookie (see RelayAuthExtensions) is what actually keeps
            // the admin signed in; this only makes the reconnect seamless.
            options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);
        });

    builder.Services.AddBlazorBlueprintComponents();

    // Show detailed Blazor circuit errors during development
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(options =>
            options.DetailedErrors = true);
    }
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<WinSmtpRelay.Storage.QueueDepthRecorder>();
    builder.Services.AddSingleton<WinSmtpRelay.Core.Interfaces.IQueueDepthRecorder>(sp => sp.GetRequiredService<WinSmtpRelay.Storage.QueueDepthRecorder>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WinSmtpRelay.Storage.QueueDepthRecorder>());
    builder.Services.AddSingleton<WinSmtpRelay.Core.Interfaces.IActivityNotifier, WinSmtpRelay.AdminApi.ActivityNotifier>();
    // In-process live-activity feed: the server-rendered Blazor pages subscribe to this directly instead
    // of a server-to-self SignalR connection (which cannot carry the signed-in user's auth/tenant).
    builder.Services.AddSingleton<WinSmtpRelay.Core.Interfaces.IActivityFeed, WinSmtpRelay.Core.InProcessActivityFeed>();
    builder.Services.AddHttpClient();
    builder.Services.AddHostedService<WinSmtpRelay.Service.TrayIconService>();
    builder.Services.AddHostedService<WinSmtpRelay.Service.StatisticsAggregator>();
    builder.Services.AddHostedService<WinSmtpRelay.Service.ReportingService>();
    // Daily self-check (setup/deliverability/journal diagnostics) + its scheduled runner.
    builder.Services.AddRelayHealthChecks();
    builder.Services.AddHostedService<HealthCheckService>();
    // Unattended software self-update (download + verify + hand to the elevated SYSTEM updater task).
    builder.Services.AddSingleton<WinSmtpRelay.Core.Interfaces.IUpdateService, WinSmtpRelay.Service.Update.UpdateService>();
    builder.Services.AddHostedService<WinSmtpRelay.Storage.ConfigurationSeeder>();
    builder.Services.AddHostedService<WinSmtpRelay.Service.AdminSeeder>();
}

var app = builder.Build();

// Auto-apply EF Core migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
    await db.Database.MigrateAsync();
}

// Apply an operator-imported admin-UI certificate (stored in the DB via the admin UI) over the startup
// default, if one is present. The provider is read by Kestrel per handshake, so this takes effect for
// new connections.
if (adminUiConfig.Enabled && adminUiConfig.UseHttps)
{
    using var scope = app.Services.CreateScope();
    try
    {
        var imported = await scope.ServiceProvider
            .GetRequiredService<WinSmtpRelay.Core.Interfaces.IAdminCertificateService>()
            .LoadImportedAsync();
        if (imported is not null)
        {
            adminCertProvider.Set(imported);
            app.Logger.LogInformation("Admin UI HTTPS: using imported certificate {Subject} (expires {Expiry:u}).",
                imported.Subject, imported.NotAfter);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Admin UI HTTPS: could not load the imported certificate — keeping the startup default.");
    }
}

if (adminUiConfig.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRelayTenantContext();
    app.UseAntiforgery();

    // Admin REST API (authorized inside MapAdminApi; /api/health is anonymous)
    app.MapAdminApi();

    // Machine-readable OpenAPI document for the REST API at /openapi/v1.json (the API shape only — the
    // endpoints still enforce API-key authentication). Point Scalar/Swagger or a client generator at it.
    app.MapOpenApi();

    // SignalR hub for live activity — authenticated admins only
    app.MapHub<ActivityHub>("/hubs/activity").RequireAuthorization();

    // Cookie sign-out endpoint (posted from the admin layout)
    app.MapPost("/account/logout", async (SignInManager<AdminUser> signInManager) =>
    {
        await signInManager.SignOutAsync();
        return Results.LocalRedirect("/account/login");
    });

    // Tenant switcher: re-issues the cookie with an updated active_tenant claim. The target is validated
    // against the principal's memberships (consent model) — a host admin selects "Global" or a tenant they
    // are a member of; a tenant user switches only among their tenant memberships. To reach a tenant they
    // are not a member of, a host admin must break-glass (which first creates the membership).
    app.MapPost("/account/switch-tenant", async (HttpContext ctx, [FromForm] string tenant) =>
    {
        var user = ctx.User;
        var identity = new ClaimsIdentity(
            user.Claims.Where(c => c.Type != RelayClaimTypes.ActiveTenant),
            user.Identity!.AuthenticationType);

        if (tenant == "all")
        {
            // Global / host scope — only a host admin has it; leave ActiveTenant unset.
            if (!RelayAccess.HasHostMembership(user))
                return Results.LocalRedirect("/");
        }
        else if (int.TryParse(tenant, out var tenantId) && RelayAccess.TenantMemberships(user).ContainsKey(tenantId))
        {
            identity.AddClaim(new Claim(RelayClaimTypes.ActiveTenant, tenantId.ToString()));
        }
        else
        {
            // Not a member of the requested tenant — ignore the switch.
            return Results.LocalRedirect("/");
        }

        await ctx.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(identity));
        return Results.LocalRedirect("/");
    }).RequireAuthorization();

    // Break-glass: a host admin self-grants tenant-admin access to a tenant they are NOT a member of.
    // It is the recovery path for an orphaned tenant (no admins). The grant is flagged IsBreakGlass and
    // written to the audit log, then the cookie is re-issued (refreshed membership claims) scoped into
    // the tenant so the host admin lands inside it.
    app.MapPost("/account/break-glass", async (HttpContext ctx, [FromForm] int tenantId, [FromForm] string? reason,
        UserManager<AdminUser> users, IAdminMembershipService memberships, IAdminAuditService audit,
        IUserClaimsPrincipalFactory<AdminUser> claimsFactory, ITenantService tenants) =>
    {
        var user = await users.GetUserAsync(ctx.User);
        if (user is null || await tenants.GetByIdAsync(tenantId) is null)
            return Results.LocalRedirect("/");

        await memberships.GrantAsync(user.Id, tenantId, RelayRoles.TenantAdmin, user.Id, breakGlass: true);
        await audit.WriteAsync(AdminAuditActions.BreakGlassEntered, user.Id, user.Email,
            targetUserId: user.Id, tenantId: tenantId, detail: reason);

        var principal = await claimsFactory.CreateAsync(user);
        if (principal.Identity is ClaimsIdentity id)
        {
            foreach (var c in id.FindAll(RelayClaimTypes.ActiveTenant).ToList())
                id.RemoveClaim(c);
            id.AddClaim(new Claim(RelayClaimTypes.ActiveTenant, tenantId.ToString()));
        }
        await ctx.SignInAsync(IdentityConstants.ApplicationScheme, principal);
        return Results.LocalRedirect("/");
    }).RequireAuthorization(AuthorizationPolicies.HostAdmin);

    // ---- Passkeys (WebAuthn). Passwordless PRIMARY sign-in via ASP.NET Core Identity (.NET 10). ----

    // Registration options for adding a passkey to your own account (authenticated).
    app.MapPost("/account/passkey/creation-options", async (HttpContext ctx, UserManager<AdminUser> um, SignInManager<AdminUser> sm) =>
    {
        var user = await um.GetUserAsync(ctx.User);
        if (user is null)
            return Results.Unauthorized();
        var userName = await um.GetUserNameAsync(user) ?? "admin";
        var optionsJson = await sm.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = await um.GetUserIdAsync(user),
            Name = userName,
            DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? userName : user.DisplayName!,
        });
        return Results.Content(optionsJson, "application/json");
    }).RequireAuthorization();

    // Verify the attestation and store the new passkey (authenticated).
    app.MapPost("/account/passkey/register", async (HttpContext ctx, UserManager<AdminUser> um, SignInManager<AdminUser> sm, IAdminAuditService audit) =>
    {
        var user = await um.GetUserAsync(ctx.User);
        if (user is null)
            return Results.Unauthorized();
        var credentialJson = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        var attestation = await sm.PerformPasskeyAttestationAsync(credentialJson);
        if (!attestation.Succeeded)
            return Results.BadRequest(attestation.Failure?.Message ?? "Could not verify the passkey.");
        var passkey = attestation.Passkey;
        passkey.Name ??= $"Passkey {DateTimeOffset.UtcNow:yyyy-MM-dd}";
        var add = await um.AddOrUpdatePasskeyAsync(user, passkey);
        if (!add.Succeeded)
            return Results.BadRequest("Could not store the passkey.");
        await audit.WriteAsync(AdminAuditActions.PasskeyAdded, user.Id, user.Email, targetUserId: user.Id, detail: passkey.Name);
        return Results.Ok();
    }).RequireAuthorization();

    // Assertion options for passwordless sign-in (anonymous, username-less / discoverable credentials).
    app.MapPost("/account/passkey/request-options", async (SignInManager<AdminUser> sm) =>
    {
        var optionsJson = await sm.MakePasskeyRequestOptionsAsync(user: null);
        return Results.Content(optionsJson, "application/json");
    }).AllowAnonymous();

    // Verify the assertion, apply the same gates as the other sign-in paths, then sign in (anonymous).
    app.MapPost("/account/passkey/signin", async (HttpContext ctx, UserManager<AdminUser> um, SignInManager<AdminUser> sm,
        IAdminMembershipService memberships, IRuntimeConfigCache cache, IAdminAuditService audit) =>
    {
        var credentialJson = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        var assertion = await sm.PerformPasskeyAssertionAsync(credentialJson);
        if (!assertion.Succeeded)
            return Results.Unauthorized();

        var user = assertion.User;
        // Same gates as password / magic-link sign-in: locked-out (e.g. pending signup) and no-usable-scope
        // accounts are refused. (SignInAsync, unlike a password check, does not enforce lockout.)
        if (await um.IsLockedOutAsync(user) ||
            !await WinSmtpRelay.AdminUi.Services.AccountAccess.HasUsableScopeAsync(memberships, cache, user.Id))
            return Results.Unauthorized();

        await um.AddOrUpdatePasskeyAsync(user, assertion.Passkey); // persist the updated sign counter
        // Persistent cookie, consistent with password sign-in (stay signed in across restarts / idle).
        await sm.SignInAsync(user, isPersistent: true);
        await audit.WriteAsync(AdminAuditActions.SignInSucceeded, user.Id, user.Email, targetUserId: user.Id, detail: "passkey");
        return Results.Ok();
    }).AllowAnonymous();

    // Remove one of your own passkeys (authenticated).
    app.MapPost("/account/passkey/remove", async (HttpContext ctx, [FromForm] string credentialId, UserManager<AdminUser> um, IAdminAuditService audit) =>
    {
        var user = await um.GetUserAsync(ctx.User);
        if (user is not null && !string.IsNullOrEmpty(credentialId))
        {
            var id = Convert.FromBase64String(credentialId.Replace('-', '+').Replace('_', '/').PadRight(credentialId.Length + (4 - credentialId.Length % 4) % 4, '='));
            await um.RemovePasskeyAsync(user, id);
            await audit.WriteAsync(AdminAuditActions.PasskeyRemoved, user.Id, user.Email, targetUserId: user.Id);
        }
        return Results.LocalRedirect("/account/passkeys");
    }).RequireAuthorization();

    // Static assets (fingerprinted CSS/JS from RCLs)
    app.MapStaticAssets();

    // Blazor Admin UI
    app.MapRazorComponents<WinSmtpRelay.AdminUi.Components.App>()
        .AddInteractiveServerRenderMode();

    if (!adminHttpsActive)
    {
        // The admin plane carries full-authority cookies/API keys. Plain HTTP is tolerated only on
        // loopback (local/dev); refuse to serve it on any network-reachable address so the credentials
        // are never interceptable on the wire. (bindsToLoopback is computed once near the Kestrel setup.)
        if (!bindsToLoopback)
            throw new InvalidOperationException(
                $"Admin UI is set to bind to '{adminUiConfig.BindAddress}' without HTTPS. The admin plane " +
                "must not be served over plain HTTP on a network-reachable address — configure " +
                "AdminUi:CertificatePath/CertificatePassword, or bind to 127.0.0.1.");
        app.Logger.LogWarning(
            "Admin UI is serving over plain HTTP on loopback — no HTTPS certificate is configured. " +
            "Set AdminUi:CertificatePath/CertificatePassword for production deployments.");
    }
    app.Logger.LogInformation("Admin UI listening on {Scheme}://{Address}:{Port}",
        adminHttpsActive ? "https" : "http", adminUiConfig.BindAddress, adminUiConfig.Port);
    if (devHttpLoopback)
        app.Logger.LogInformation("Admin UI also listening on http://127.0.0.1:{Port} (Development only, loopback — password manager works here)",
            adminUiConfig.Port + 1);
}

await app.RunAsync();

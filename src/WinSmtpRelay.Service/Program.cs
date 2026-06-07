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

// Storage
var connectionString = builder.Configuration.GetConnectionString("RelayDb") ?? "Data Source=winsmtprelay.db";
builder.Services.AddRelayStorage(connectionString);

// SMTP Listener
builder.Services.AddSmtpListener();

// Delivery Engine
builder.Services.AddDeliveryEngine();

// Service state reporter — writes registry flag for MSI upgrade detection
builder.Services.AddHostedService<WinSmtpRelay.Service.ServiceStateReporter>();

// Kestrel for Admin UI + API
var adminUiConfig = builder.Configuration.GetSection(AdminUiOptions.SectionName).Get<AdminUiOptions>() ?? new();

// Resolve the admin HTTPS certificate up front: a configured PFX if set, otherwise a persistent
// self-signed certificate generated next to the service binaries. This keeps the admin plane on HTTPS
// out of the box — a self-signed cert just means a one-time browser warning until a trusted certificate
// is imported via the admin UI.
System.Security.Cryptography.X509Certificates.X509Certificate2? adminCert = null;
if (adminUiConfig.Enabled && adminUiConfig.UseHttps)
{
    using var certLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    adminCert = WinSmtpRelay.Security.AdminUiCertificate.Resolve(
        adminUiConfig, AppContext.BaseDirectory,
        certLoggerFactory.CreateLogger("WinSmtpRelay.AdminUiCertificate"));
}

// HTTPS is active when UseHttps is on and a certificate is available (configured/self-signed), or the
// ASP.NET Core dev certificate in Development.
var adminHttpsActive = adminUiConfig.UseHttps &&
    (adminCert is not null || builder.Environment.IsDevelopment());

if (adminUiConfig.Enabled)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(System.Net.IPAddress.Parse(adminUiConfig.BindAddress), adminUiConfig.Port, listen =>
        {
            if (!adminUiConfig.UseHttps)
                return; // explicit opt-out → serve HTTP

            if (adminCert is not null)
                listen.UseHttps(adminCert);
            else if (builder.Environment.IsDevelopment())
                listen.UseHttps(); // ASP.NET Core development certificate
            // else: certificate resolution failed (logged above) and not Development — HTTPS not bound
        });
    });

    // Admin authentication/authorization (cookie + API key) and tenant-aware policies.
    builder.Services.AddRelayAdminAuth();
    builder.Services.AddRelayAdminUiAuth();

    // DNS setup / domain-authentication checks. Public-record checks (hostname A/AAAA, MX, PTR,
    // SPF/DKIM/DMARC TXT) go through a public resolver (8.8.8.8/1.1.1.1) to avoid split-horizon answers;
    // the DNSBL check stays on the host's own ILookupClient (Spamhaus refuses public resolvers).
    builder.Services.AddSingleton<WinSmtpRelay.Security.PublicDnsLookupClient>();
    builder.Services.AddScoped<WinSmtpRelay.Core.Interfaces.IDnsSetupService, WinSmtpRelay.Security.DnsSetupService>();
    // Public Suffix List lookup (embedded snapshot, parsed once) for registrable-domain derivation.
    builder.Services.AddSingleton<WinSmtpRelay.Core.Interfaces.IPublicSuffixService, WinSmtpRelay.Security.PublicSuffixService>();

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

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

if (adminUiConfig.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRelayTenantContext();
    app.UseAntiforgery();

    // Admin REST API (authorized inside MapAdminApi; /api/health is anonymous)
    app.MapAdminApi();

    // SignalR hub for live activity — authenticated admins only
    app.MapHub<ActivityHub>("/hubs/activity").RequireAuthorization();

    // Cookie sign-out endpoint (posted from the admin layout)
    app.MapPost("/account/logout", async (SignInManager<AdminUser> signInManager) =>
    {
        await signInManager.SignOutAsync();
        return Results.LocalRedirect("/account/login");
    });

    // Host-admin tenant switcher: re-issues the cookie with an updated active_tenant claim.
    app.MapPost("/account/switch-tenant", async (HttpContext ctx, [FromForm] string tenant, ITenantService tenants) =>
    {
        var identity = new ClaimsIdentity(
            ctx.User.Claims.Where(c => c.Type != RelayClaimTypes.ActiveTenant),
            ctx.User.Identity!.AuthenticationType);

        if (tenant != "all" && int.TryParse(tenant, out var tenantId) && await tenants.GetByIdAsync(tenantId) is not null)
            identity.AddClaim(new Claim(RelayClaimTypes.ActiveTenant, tenantId.ToString()));

        await ctx.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(identity));
        return Results.LocalRedirect("/");
    }).RequireAuthorization(AuthorizationPolicies.HostAdmin);

    // Static assets (fingerprinted CSS/JS from RCLs)
    app.MapStaticAssets();

    // Blazor Admin UI
    app.MapRazorComponents<WinSmtpRelay.AdminUi.Components.App>()
        .AddInteractiveServerRenderMode();

    if (!adminHttpsActive)
    {
        // The admin plane carries full-authority cookies/API keys. Plain HTTP is tolerated only on
        // loopback (local/dev); refuse to serve it on any network-reachable address so the credentials
        // are never interceptable on the wire.
        var bindsToLoopback = System.Net.IPAddress.TryParse(adminUiConfig.BindAddress, out var bindIp)
            && System.Net.IPAddress.IsLoopback(bindIp);
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
}

await app.RunAsync();

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.AdminApi;
using WinSmtpRelay.AdminApi.Auth;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Integration.Tests;

/// <summary>
/// The 2026-11 review fixes of the admin API: host-only rate limits (H12), message-body consent + audit (T1081),
/// request DTOs and validation (M19), the API-key ceiling and failure throttle, paging caps, security headers,
/// the sign-in rate limit, and the passwordless sign-in handing off to two-factor (T1080).
/// </summary>
[TestClass]
public class AdminApiHardeningTests
{
    private static readonly string AllWriteScopes =
        string.Join(' ', ApiKeyScopes.Areas.Select(ApiKeyScopes.Write).Append(ApiKeyScopes.MessagesBody));

    private WebApplication _app = null!;
    private string _dbPath = null!;
    private Uri _base = null!;
    private readonly List<HttpClient> _clients = [];

    private HttpClient _host = null!;         // HostAdmin key, every scope incl. messages:body
    private HttpClient _tenantAdmin = null!;  // TenantAdmin key of the default tenant, every scope
    private HttpClient _tenant2Admin = null!; // TenantAdmin key of tenant 2, every scope
    private HttpClient _viewer = null!;       // TenantViewer key of the default tenant, read-only
    private HttpClient _adminOnly = null!;    // HostAdmin key with admin:write only

    [TestInitialize]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"adminhardening_{Guid.NewGuid()}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRelayStorage($"Data Source={_dbPath}");
        builder.Services.AddRelayAdminAuth();
        builder.Services.AddRelaySignInRateLimiting(postsPerIpPerMinute: 3);
        _app = builder.Build();

        string host, tenantAdmin, tenant2Admin, viewer, adminOnly;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            await db.Database.MigrateAsync();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AdminRole>>();
            foreach (var role in RelayRoles.All)
                if (!await roles.RoleExistsAsync(role))
                    await roles.CreateAsync(new AdminRole(role));
            db.Tenants.Add(new Tenant { Id = 2, Name = "Tenant Two", Slug = "tenant-two" });
            await db.SaveChangesAsync();

            var keys = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
            (_, host) = await keys.CreateAsync(null, "host", RelayRoles.HostAdmin, AllWriteScopes, null, default);
            (_, tenantAdmin) = await keys.CreateAsync(TenantDefaults.DefaultTenantId, "t1-admin", RelayRoles.TenantAdmin, AllWriteScopes, null, default);
            (_, tenant2Admin) = await keys.CreateAsync(2, "t2-admin", RelayRoles.TenantAdmin, AllWriteScopes, null, default);
            (_, viewer) = await keys.CreateAsync(TenantDefaults.DefaultTenantId, "t1-viewer", RelayRoles.TenantViewer, null, null, default);
            (_, adminOnly) = await keys.CreateAsync(null, "admin-only", RelayRoles.HostAdmin, "admin:write", null, default);
        }

        _app.UseRelaySecurityHeaders();
        _app.UseRateLimiter();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseRelayTenantContext();
        _app.MapAdminApi();

        // Probe endpoints for the sign-in throttle and the passwordless two-factor hand-off.
        _app.MapPost("/test/signin-probe", () => Results.Ok()).AllowAnonymous().RequireRateLimiting(RelayRateLimitPolicies.SignIn);
        _app.MapGet("/test/signin-probe", () => Results.Ok()).AllowAnonymous().RequireRateLimiting(RelayRateLimitPolicies.SignIn);
        _app.MapPost("/test/passwordless/{userId:int}", async (int userId, SignInManager<AdminUser> sm) =>
        {
            var user = await sm.UserManager.FindByIdAsync(userId.ToString());
            var result = await ((RelaySignInManager)sm).PasswordlessSignInOrTwoFactorAsync(user!, isPersistent: false);
            return Results.Ok(new { result.Succeeded, result.RequiresTwoFactor });
        }).AllowAnonymous();

        await _app.StartAsync();
        _base = new Uri(_app.Urls.First());

        _host = NewClient(host);
        _tenantAdmin = NewClient(tenantAdmin);
        _tenant2Admin = NewClient(tenant2Admin);
        _viewer = NewClient(viewer);
        _adminOnly = NewClient(adminOnly);
    }

    private HttpClient NewClient(string? key)
    {
        var client = new HttpClient { BaseAddress = _base };
        if (key is not null)
            client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, key);
        _clients.Add(client);
        return client;
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var c in _clients)
            c.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task<long> EnqueueAsync(int tenantId)
    {
        using var scope = _app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentTenant>().SetTenant(tenantId);
        return await scope.ServiceProvider.GetRequiredService<IMessageQueue>().EnqueueAsync(new QueuedMessage
        {
            TenantId = tenantId,
            MessageId = $"<{Guid.NewGuid()}@example.com>",
            Sender = "sender@example.com",
            Recipients = "recipient@example.com",
            RawMessage = "Subject: secret\r\n\r\nbody"u8.ToArray(),
            SizeBytes = 25,
        });
    }

    private async Task<List<AdminAuditEvent>> AuditAsync(string action)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RelayDbContext>().AdminAuditEvents.AsNoTracking()
            .Where(e => e.Action == action).ToListAsync();
    }

    private static HttpRequestMessage WithTenant(HttpMethod method, string url, string tenant, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(RelayTenantContextMiddleware.TenantHeader, tenant);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    // ---- H12 ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RateLimits_AreHostOnly_TenantAdminKeyIsForbidden()
    {
        var settings = new RateLimitSettingsRequest(30, 20, 1000, 5, 30);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _tenantAdmin.PutAsJsonAsync("/api/rate-limits", settings)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _tenantAdmin.GetAsync("/api/rate-limits")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _host.PutAsJsonAsync("/api/rate-limits", settings)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _host.PutAsJsonAsync("/api/rate-limits", settings with { MaxConnectionsPerIpPerMinute = -1 })).StatusCode);
    }

    // ---- T1081 / M17 ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MessageBody_HostKey_IsForbidden_InHostScope_AndWhenNarrowedToTheTenant()
    {
        var id = await EnqueueAsync(TenantDefaults.DefaultTenantId);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await _host.GetAsync($"/api/queue/messages/{id}/body")).StatusCode);
        var narrowed = await _host.SendAsync(WithTenant(HttpMethod.Get, $"/api/queue/messages/{id}/body", TenantDefaults.DefaultSlug));
        Assert.AreEqual(HttpStatusCode.Forbidden, narrowed.StatusCode);
        Assert.AreEqual(0, (await AuditAsync(AdminAuditActions.MessageBodyRead)).Count, "a refused read is not a read");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MessageBody_TenantAdminKeyOfThatTenant_ReadsIt_AndTheReadIsAudited()
    {
        var id = await EnqueueAsync(TenantDefaults.DefaultTenantId);

        var response = await _tenantAdmin.GetAsync($"/api/queue/messages/{id}/body");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Subject: secret");

        var audit = (await AuditAsync(AdminAuditActions.MessageBodyRead)).Single();
        Assert.IsNotNull(audit.ActorApiKeyId);
        Assert.AreEqual("t1-admin", audit.ActorEmail);
        Assert.AreEqual(TenantDefaults.DefaultTenantId, audit.TenantId);
        StringAssert.Contains(audit.Detail, $"message {id}");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MessageBody_OtherTenantsKey_SeesNotFound_ViewerIsForbidden()
    {
        var id = await EnqueueAsync(TenantDefaults.DefaultTenantId);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _tenant2Admin.GetAsync($"/api/queue/messages/{id}/body")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _viewer.GetAsync($"/api/queue/messages/{id}/body")).StatusCode);
    }

    // ---- M19 ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task IpRule_InvalidCidr_IsBadRequest_ClientIdIsIgnored_UnknownIdIs404()
    {
        var bad = await _tenantAdmin.PostAsJsonAsync("/api/ip-rules", new { network = "10.0.0.0/33" });
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await _tenantAdmin.PostAsJsonAsync("/api/ip-rules",
            new { id = 4242, tenantId = 2, createdUtc = "2000-01-01T00:00:00Z", network = "192.168.1.10/32", action = 0 });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var rule = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreNotEqual(4242, rule.GetProperty("id").GetInt32());
        Assert.AreEqual(TenantDefaults.DefaultTenantId, rule.GetProperty("tenantId").GetInt32());
        Assert.AreNotEqual(2000, rule.GetProperty("createdUtc").GetDateTimeOffset().Year);

        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _tenantAdmin.PutAsJsonAsync("/api/ip-rules/999999", new { network = "10.1.1.1" })).StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SendConnector_PutWithoutPassword_KeepsIt_EmptyClearsIt_NeverReturned()
    {
        var create = await _tenantAdmin.PostAsJsonAsync("/api/connectors/send",
            new SendConnectorRequest("Brevo", "smtp-relay.example.com", 587, "user", "s3cret-key"));
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        var body = await create.Content.ReadAsStringAsync();
        Assert.IsFalse(body.Contains("s3cret-key"), "the password must never be echoed");
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32();

        var keep = await _tenantAdmin.PutAsJsonAsync($"/api/connectors/send/{id}",
            new SendConnectorRequest("Brevo renamed", "smtp-relay.example.com", 587, "user", Password: null, EhloDomain: "relay.example.com"));
        Assert.AreEqual(HttpStatusCode.OK, keep.StatusCode);
        var stored = await StoredConnectorAsync(id);
        Assert.AreEqual("s3cret-key", stored.EncryptedPassword);
        Assert.AreEqual("relay.example.com", stored.EhloDomain, "the EHLO domain is persisted on update");

        await _tenantAdmin.PutAsJsonAsync($"/api/connectors/send/{id}",
            new SendConnectorRequest("Brevo renamed", "smtp-relay.example.com", 587, "user", Password: ""));
        Assert.IsNull((await StoredConnectorAsync(id)).EncryptedPassword);

        Assert.AreEqual(HttpStatusCode.BadRequest, (await _tenantAdmin.PostAsJsonAsync("/api/connectors/send",
            new SendConnectorRequest("Bad", "smtp.example.com", 70000))).StatusCode);
    }

    private async Task<SendConnector> StoredConnectorAsync(int id)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RelayDbContext>().SendConnectors
            .IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == id);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DkimDomain_CreateResponse_NeverEchoesThePrivateKey()
    {
        const string pem = "-----BEGIN PRIVATE KEY-----\nMIIBVQIBADANBgkqhkiG9w0BAQEFAASCAT8wggE7AgEAAkEA\n-----END PRIVATE KEY-----";
        var response = await _host.SendAsync(WithTenant(HttpMethod.Post, "/api/dkim/domains", TenantDefaults.DefaultSlug,
            new DkimDomainRequest("signed.example", "sel1", pem)));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(text.Contains("PRIVATE KEY", StringComparison.Ordinal), text);
        StringAssert.Contains(text, "\"hasPrivateKey\":true");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Paging_And_KeySize_AreValidated()
    {
        Assert.AreEqual(HttpStatusCode.BadRequest, (await _host.GetAsync("/api/deliverylogs?offset=-1")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _host.GetAsync("/api/deliverylogs?limit=1000000")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _host.GetAsync("/api/queue/messages?limit=1000000")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await _host.PostAsJsonAsync("/api/dkim/generate",
            new DkimGenerateRequest("example.com", "sel1", 1024))).StatusCode);
    }

    // ---- API key ceiling ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ApiKey_AdminWriteKey_CannotMintOrRescopeWiderKeys()
    {
        var wider = await _adminOnly.PostAsJsonAsync("/api/apikeys",
            new CreateApiKeyRequest("wider", RelayRoles.HostAdmin, null, null, "config:write"));
        Assert.AreEqual(HttpStatusCode.Forbidden, wider.StatusCode);

        var scopeless = await _adminOnly.PostAsJsonAsync("/api/apikeys",
            new CreateApiKeyRequest("reads-everything", RelayRoles.HostAdmin, null, null, null));
        Assert.AreEqual(HttpStatusCode.Forbidden, scopeless.StatusCode, "a scope-less key reads every area");

        var within = await _adminOnly.PostAsJsonAsync("/api/apikeys",
            new CreateApiKeyRequest("narrow", RelayRoles.TenantAdmin, TenantDefaults.DefaultTenantId, null, "admin:read"));
        Assert.AreEqual(HttpStatusCode.OK, within.StatusCode);
        var id = (await within.Content.ReadFromJsonAsync<CreatedApiKeyResponse>())!.Id;

        var rescope = await _adminOnly.PutAsJsonAsync($"/api/apikeys/{id}", new UpdateApiKeyRequest("messages:body"));
        Assert.AreEqual(HttpStatusCode.Forbidden, rescope.StatusCode);

        // A tenant-admin key cannot mint a host key at all.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _tenantAdmin.PostAsJsonAsync("/api/apikeys",
            new CreateApiKeyRequest("escalate", RelayRoles.HostAdmin, null, null, "diag:read"))).StatusCode);
    }

    // ---- API key failure throttle ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ApiKey_RepeatedFailures_AreAnswered429_BeforeTheLookup()
    {
        using var guesser = NewClient("wsr_notavalidkeyatall0000000000000000000000000");
        for (var i = 0; i < ApiKeyFailureThrottle.MaxFailures; i++)
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await guesser.GetAsync("/api/queue/status")).StatusCode);

        var blocked = await guesser.GetAsync("/api/queue/status");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.IsTrue(blocked.Headers.RetryAfter is not null);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ApiKey_LastUsed_IsWrittenAtMostOncePerMinute()
    {
        await _viewer.GetAsync("/api/queue/status");
        var first = await LastUsedAsync("t1-viewer");
        await _viewer.GetAsync("/api/queue/status");
        Assert.AreEqual(first, await LastUsedAsync("t1-viewer"));
    }

    private async Task<DateTimeOffset?> LastUsedAsync(string name)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RelayDbContext>().ApiKeys.AsNoTracking()
            .Where(k => k.Name == name).Select(k => k.LastUsedUtc).SingleAsync();
    }

    // ---- headers + sign-in throttle ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Responses_CarryTheSecurityHeaders()
    {
        var response = await _host.GetAsync("/api/health");
        Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.AreEqual("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.AreEqual("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        StringAssert.Contains(response.Headers.GetValues("Content-Security-Policy").Single(), "frame-ancestors 'none'");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SignInPosts_AreThrottledPerIp_PageViewsAreNot()
    {
        using var anon = NewClient(null);
        for (var i = 0; i < 3; i++)
            Assert.AreEqual(HttpStatusCode.OK, (await anon.PostAsync("/test/signin-probe", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await anon.PostAsync("/test/signin-probe", null)).StatusCode);
        for (var i = 0; i < 5; i++)
            Assert.AreEqual(HttpStatusCode.OK, (await anon.GetAsync("/test/signin-probe")).StatusCode);
    }

    // ---- T1080 ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PasswordlessSignIn_WithTwoFactorEnabled_RequiresTheSecondFactor()
    {
        int withMfa, withoutMfa;
        using (var scope = _app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            var a = new AdminUser { UserName = "mfa@example.com", Email = "mfa@example.com" };
            var b = new AdminUser { UserName = "plain@example.com", Email = "plain@example.com" };
            await users.CreateAsync(a, "Passw0rdPassw0rd");
            await users.CreateAsync(b, "Passw0rdPassw0rd");
            // As TwoFactor.razor does: an authenticator key, then two-factor on.
            await users.ResetAuthenticatorKeyAsync(a);
            await users.SetTwoFactorEnabledAsync(a, true);
            (withMfa, withoutMfa) = (a.Id, b.Id);
        }

        using var anon = NewClient(null);
        var mfa = await anon.PostAsync($"/test/passwordless/{withMfa}", null);
        var mfaResult = await mfa.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(mfaResult.GetProperty("requiresTwoFactor").GetBoolean());
        Assert.IsFalse(mfaResult.GetProperty("succeeded").GetBoolean());
        var cookies = string.Join(";", mfa.Headers.TryGetValues("Set-Cookie", out var v) ? v : []);
        StringAssert.Contains(cookies, "Identity.TwoFactorUserId");
        Assert.IsFalse(cookies.Contains("WinSmtpRelay.Admin="), "no session cookie before the second factor");

        var plain = await (await anon.PostAsync($"/test/passwordless/{withoutMfa}", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(plain.GetProperty("succeeded").GetBoolean());
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

[TestClass]
public class AdminApiTests
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;       // authenticated as HostAdmin (API key)
    private HttpClient _anonClient = null!;   // no credentials
    private HttpClient _viewerClient = null!; // authenticated as TenantViewer (read-only)
    private string _dbPath = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"adminapi_test_{Guid.NewGuid()}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRelayStorage($"Data Source={_dbPath}");
        builder.Services.AddRelayAdminAuth();

        _app = builder.Build();

        string hostKey, viewerKey;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            await db.Database.MigrateAsync();

            // Seed admin roles (the real app does this in AdminSeeder).
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AdminRole>>();
            foreach (var role in RelayRoles.All)
                if (!await roleManager.RoleExistsAsync(role))
                    await roleManager.CreateAsync(new AdminRole(role));

            var keys = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
            (_, hostKey) = await keys.CreateAsync(null, "test-host", RelayRoles.HostAdmin, null, default);
            (_, viewerKey) = await keys.CreateAsync(TenantDefaults.DefaultTenantId, "test-viewer", RelayRoles.TenantViewer, null, default);
        }

        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseRelayTenantContext();
        _app.MapAdminApi();

        await _app.StartAsync();

        var address = _app.Urls.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
        _client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, hostKey);

        _anonClient = new HttpClient { BaseAddress = new Uri(address) };

        _viewerClient = new HttpClient { BaseAddress = new Uri(address) };
        _viewerClient.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, viewerKey);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        _anonClient.Dispose();
        _viewerClient.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.AreEqual("Healthy", body?.Status);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task QueueStatus_ReturnsDepth()
    {
        var response = await _client.GetAsync("/api/queue/status");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<QueueStatusResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(0, body.Depth);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task QueueMessages_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/queue/messages");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var messages = await response.Content.ReadFromJsonAsync<MessageSummary[]>();
        Assert.IsNotNull(messages);
        Assert.AreEqual(0, messages.Length);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task QueueMessage_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/queue/messages/999");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task QueueMessage_EnqueueAndRetrieve()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();
            await queue.EnqueueAsync(new QueuedMessage
            {
                MessageId = "<test@example.com>",
                Sender = "sender@example.com",
                Recipients = "recipient@example.com",
                RawMessage = "From: test"u8.ToArray(),
                SizeBytes = 10
            });
        }

        var statusResponse = await _client.GetAsync("/api/queue/status");
        var status = await statusResponse.Content.ReadFromJsonAsync<QueueStatusResponse>();
        Assert.AreEqual(1, status?.Depth);

        var messagesResponse = await _client.GetAsync("/api/queue/messages");
        var messages = await messagesResponse.Content.ReadFromJsonAsync<MessageSummary[]>();
        Assert.AreEqual(1, messages?.Length);
        Assert.AreEqual("sender@example.com", messages?[0].Sender);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task QueueMessage_Delete_RemovesMessage()
    {
        long msgId;
        using (var scope = _app.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();
            msgId = await queue.EnqueueAsync(new QueuedMessage
            {
                MessageId = "<delete@example.com>",
                Sender = "sender@example.com",
                Recipients = "recipient@example.com",
                RawMessage = "From: test"u8.ToArray(),
                SizeBytes = 10
            });
        }

        var deleteResponse = await _client.DeleteAsync($"/api/queue/messages/{msgId}");
        Assert.AreEqual(HttpStatusCode.OK, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/queue/messages/{msgId}");
        Assert.AreEqual(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Users_CreateAndList()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/users",
            new CreateUserRequest("testuser", "P@ssw0rd!"));
        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);

        var listResponse = await _client.GetAsync("/api/users");
        var users = await listResponse.Content.ReadFromJsonAsync<UserSummary[]>();
        Assert.AreEqual(1, users?.Length);
        Assert.AreEqual("testuser", users?[0].Username);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Users_CreateDuplicate_ReturnsConflict()
    {
        await _client.PostAsJsonAsync("/api/users",
            new CreateUserRequest("alice", "pass1"));

        var response = await _client.PostAsJsonAsync("/api/users",
            new CreateUserRequest("alice", "pass2"));
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Users_Delete_RemovesUser()
    {
        await _client.PostAsJsonAsync("/api/users",
            new CreateUserRequest("tobedeleted", "pass"));

        var listResponse = await _client.GetAsync("/api/users");
        var users = await listResponse.Content.ReadFromJsonAsync<UserSummary[]>();
        var userId = users![0].Id;

        var deleteResponse = await _client.DeleteAsync($"/api/users/{userId}");
        Assert.AreEqual(HttpStatusCode.OK, deleteResponse.StatusCode);

        listResponse = await _client.GetAsync("/api/users");
        users = await listResponse.Content.ReadFromJsonAsync<UserSummary[]>();
        Assert.AreEqual(0, users?.Length);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ServerInfo_ReturnsVersionAndRuntime()
    {
        var response = await _client.GetAsync("/api/server/info");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("runtime", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(body.Contains(".NET", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Authentication / authorization regression tests ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Health_IsAnonymous()
    {
        var response = await _anonClient.GetAsync("/api/health");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Unauthenticated_GetUsers_Returns401()
    {
        var response = await _anonClient.GetAsync("/api/users");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Unauthenticated_CreateUser_Returns401()
    {
        var response = await _anonClient.PostAsJsonAsync("/api/users",
            new CreateUserRequest("intruder", "P@ssw0rd!"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Unauthenticated_DeleteQueueMessage_Returns401()
    {
        var response = await _anonClient.DeleteAsync("/api/queue/messages/1");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task InvalidApiKey_Returns401()
    {
        using var client = new HttpClient { BaseAddress = _client.BaseAddress };
        client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, "wsr_thisisnotavalidkey");
        var response = await client.GetAsync("/api/users");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Viewer_CanRead_ButCannotMutate()
    {
        var read = await _viewerClient.GetAsync("/api/users");
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);

        var write = await _viewerClient.PostAsJsonAsync("/api/users",
            new CreateUserRequest("blocked", "P@ssw0rd!"));
        Assert.AreEqual(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ApiKey_TenantScope_IsolatesData()
    {
        // Seed a second tenant and a tenant-scoped admin key for it.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            db.Tenants.Add(new Tenant { Id = 2, Name = "Tenant Two", Slug = "tenant-two" });
            await db.SaveChangesAsync();
        }

        string tenant2Key;
        using (var scope = _app.Services.CreateScope())
        {
            var keys = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
            (_, tenant2Key) = await keys.CreateAsync(2, "tenant2-admin", RelayRoles.TenantAdmin, null, default);
        }

        using var t2 = new HttpClient { BaseAddress = _client.BaseAddress };
        t2.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, tenant2Key);

        // Host admin creates a user (default tenant); tenant-2 admin creates a user (stamped tenant 2).
        var created1 = await _client.PostAsJsonAsync("/api/users", new CreateUserRequest("alice_t1", "P@ssw0rd!"));
        Assert.AreEqual(HttpStatusCode.Created, created1.StatusCode);
        var created2 = await t2.PostAsJsonAsync("/api/users", new CreateUserRequest("bob_t2", "P@ssw0rd!"));
        Assert.AreEqual(HttpStatusCode.Created, created2.StatusCode);

        // Tenant-2 admin sees only its own tenant's user.
        var t2Users = await t2.GetFromJsonAsync<UserSummary[]>("/api/users");
        Assert.AreEqual(1, t2Users!.Length);
        Assert.AreEqual("bob_t2", t2Users[0].Username);

        // Host admin (all-tenants scope) sees both.
        var hostUsers = await _client.GetFromJsonAsync<UserSummary[]>("/api/users");
        Assert.IsTrue(hostUsers!.Any(u => u.Username == "alice_t1"));
        Assert.IsTrue(hostUsers.Any(u => u.Username == "bob_t2"));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task TenantAdmin_CannotAccessAnotherTenantsUserById()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            db.Tenants.Add(new Tenant { Id = 2, Name = "Tenant Two", Slug = "tenant-two" });
            await db.SaveChangesAsync();
        }

        string tenant2Key;
        using (var scope = _app.Services.CreateScope())
        {
            var keys = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
            (_, tenant2Key) = await keys.CreateAsync(2, "tenant2-admin", RelayRoles.TenantAdmin, null, default);
        }

        using var t2 = new HttpClient { BaseAddress = _client.BaseAddress };
        t2.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, tenant2Key);

        // Host creates a user that belongs to the default tenant.
        await _client.PostAsJsonAsync("/api/users", new CreateUserRequest("alice_t1", "P@ssw0rd!"));
        var hostUsers = await _client.GetFromJsonAsync<UserSummary[]>("/api/users");
        var aliceId = hostUsers!.Single(u => u.Username == "alice_t1").Id;

        // Tenant-2 admin cannot update alice by id (the filtered lookup yields nothing -> 404),
        // proving Find-by-id no longer bypasses the tenant filter.
        var put = await t2.PutAsJsonAsync($"/api/users/{aliceId}",
            new UpdateUserRequest(false, null, null, null));
        Assert.AreEqual(HttpStatusCode.NotFound, put.StatusCode);

        // Tenant-2 admin's delete is a tenant-scoped no-op: alice still exists for the host.
        await t2.DeleteAsync($"/api/users/{aliceId}");
        var afterDelete = await _client.GetFromJsonAsync<UserSummary[]>("/api/users");
        Assert.IsTrue(afterDelete!.Any(u => u.Id == aliceId));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Tenants_HostCanManage_NonHostForbidden()
    {
        // Host admin can create + list tenants.
        var create = await _client.PostAsJsonAsync("/api/tenants", new CreateTenantRequest("Acme Corp", "acme"));
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);

        var list = await _client.GetFromJsonAsync<Tenant[]>("/api/tenants");
        Assert.IsTrue(list!.Any(t => t.Slug == "acme"));

        // Duplicate slug -> Conflict.
        var dup = await _client.PostAsJsonAsync("/api/tenants", new CreateTenantRequest("Acme 2", "acme"));
        Assert.AreEqual(HttpStatusCode.Conflict, dup.StatusCode);

        // A tenant-scoped (non-host) principal is forbidden from tenant administration.
        var viewerList = await _viewerClient.GetAsync("/api/tenants");
        Assert.AreEqual(HttpStatusCode.Forbidden, viewerList.StatusCode);

        var viewerCreate = await _viewerClient.PostAsJsonAsync("/api/tenants", new CreateTenantRequest("X", "x"));
        Assert.AreEqual(HttpStatusCode.Forbidden, viewerCreate.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Signup_CreatesPendingTenant_HostApprovalActivates()
    {
        using var scope = _app.Services.CreateScope();
        var signup = scope.ServiceProvider.GetRequiredService<ITenantSignupService>();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();

        var result = await signup.SignUpAsync("Acme", "acme-co", "owner@acme.test", "P@ssword12345");
        Assert.IsTrue(result.Succeeded, result.Error);

        // Tenant created disabled; admin unverified and locked out.
        var tenant = await db.Tenants.FirstAsync(t => t.Id == result.TenantId);
        Assert.IsFalse(tenant.IsEnabled);
        var user = await users.FindByIdAsync(result.UserId.ToString());
        Assert.IsNotNull(user);
        Assert.IsFalse(user!.EmailConfirmed);
        Assert.IsTrue(await users.IsLockedOutAsync(user));

        // Host approval activates the tenant and unlocks/confirms the admin.
        await signup.ApproveTenantAsync(result.TenantId);
        Assert.IsTrue((await db.Tenants.FirstAsync(t => t.Id == result.TenantId)).IsEnabled);
        var activated = await users.FindByIdAsync(result.UserId.ToString());
        Assert.IsTrue(activated!.EmailConfirmed);
        Assert.IsFalse(await users.IsLockedOutAsync(activated));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Signup_EmailTokenConfirmation_Activates()
    {
        using var scope = _app.Services.CreateScope();
        var signup = scope.ServiceProvider.GetRequiredService<ITenantSignupService>();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();

        var result = await signup.SignUpAsync("Beta Inc", "beta-inc", "owner@beta.test", "P@ssword12345");
        Assert.IsTrue(result.Succeeded, result.Error);

        var confirmed = await signup.ConfirmAsync(result.UserId, result.ConfirmToken!);
        Assert.IsTrue(confirmed);
        Assert.IsTrue((await db.Tenants.FirstAsync(t => t.Id == result.TenantId)).IsEnabled);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Signup_DuplicateSlug_Fails()
    {
        using var scope = _app.Services.CreateScope();
        var signup = scope.ServiceProvider.GetRequiredService<ITenantSignupService>();

        var first = await signup.SignUpAsync("Gamma", "gamma", "a@gamma.test", "P@ssword12345");
        Assert.IsTrue(first.Succeeded, first.Error);

        var dup = await signup.SignUpAsync("Gamma 2", "gamma", "b@gamma.test", "P@ssword12345");
        Assert.IsFalse(dup.Succeeded);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ApiKey_ForDisabledTenant_FailsValidation()
    {
        int tenantId;
        string plaintext;
        using (var scope = _app.Services.CreateScope())
        {
            var tenant = await scope.ServiceProvider.GetRequiredService<ITenantService>().CreateAsync("Keyed Co", "keyed-co");
            tenantId = tenant.Id;
            var keys = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
            (_, plaintext) = await keys.CreateAsync(tenantId, "k", RelayRoles.TenantAdmin, null, default);
        }

        // Validates while the tenant is enabled.
        using (var scope = _app.Services.CreateScope())
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IApiKeyService>().ValidateAsync(plaintext, default));

        // Disabling the tenant invalidates the key.
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ITenantService>().UpdateAsync(tenantId, "Keyed Co", isEnabled: false);
        using (var scope = _app.Services.CreateScope())
            Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IApiKeyService>().ValidateAsync(plaintext, default));

        // Re-enabling restores it.
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ITenantService>().UpdateAsync(tenantId, "Keyed Co", isEnabled: true);
        using (var scope = _app.Services.CreateScope())
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IApiKeyService>().ValidateAsync(plaintext, default));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task TenantService_CannotDisableDefaultTenant()
    {
        using var scope = _app.Services.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantService>();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => tenants.UpdateAsync(TenantDefaults.DefaultTenantId, "Default", isEnabled: false));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RuntimeConfigCache_ReflectsTenantEnabledState_AfterInvalidation()
    {
        int tenantId;
        using (var scope = _app.Services.CreateScope())
            tenantId = (await scope.ServiceProvider.GetRequiredService<ITenantService>().CreateAsync("Toggle Co", "toggle-co")).Id;

        var cache = _app.Services.GetRequiredService<IRuntimeConfigCache>();
        Assert.IsTrue(await cache.IsTenantEnabledAsync(tenantId));

        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ITenantService>().UpdateAsync(tenantId, "Toggle Co", isEnabled: false);
        Assert.IsFalse(await cache.IsTenantEnabledAsync(tenantId), "cache should reflect the disable after invalidation");

        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ITenantService>().UpdateAsync(tenantId, "Toggle Co", isEnabled: true);
        Assert.IsTrue(await cache.IsTenantEnabledAsync(tenantId), "cache should reflect the re-enable after invalidation");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RateLimitSettings_CacheReflectsUpdate_AfterInvalidation()
    {
        var cache = _app.Services.GetRequiredService<IRuntimeConfigCache>();
        var before = await cache.GetRateLimitSettingsAsync();

        using (var scope = _app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IRateLimitSettingsService>().UpdateAsync(new RateLimitSettings
            {
                MaxConnectionsPerIpPerMinute = before.MaxConnectionsPerIpPerMinute + 7,
                MaxMessagesPerSenderPerMinute = before.MaxMessagesPerSenderPerMinute,
                MaxMessagesPerSenderPerDay = before.MaxMessagesPerSenderPerDay,
                FailedAuthBanThreshold = before.FailedAuthBanThreshold,
                FailedAuthBanMinutes = before.FailedAuthBanMinutes
            });
        }

        var after = await cache.GetRateLimitSettingsAsync();
        Assert.AreEqual(before.MaxConnectionsPerIpPerMinute + 7, after.MaxConnectionsPerIpPerMinute,
            "the cache should reflect the persisted update after invalidation");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Statistics_AreAggregatedAndScopedPerTenant()
    {
        var date = new DateOnly(2026, 5, 1);
        var stamp = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
        var tenantA = TenantDefaults.DefaultTenantId;
        int tenantB;

        using (var scope = _app.Services.CreateScope())
        {
            tenantB = (await scope.ServiceProvider.GetRequiredService<ITenantService>().CreateAsync("Stats Co", "stats-co")).Id;
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            // Two delivered for tenant A, one for tenant B, on the same day.
            db.DeliveryLogs.Add(new DeliveryLog { TenantId = tenantA, Recipient = "a1@x", StatusCode = "250", StatusMessage = "ok", TimestampUtc = stamp });
            db.DeliveryLogs.Add(new DeliveryLog { TenantId = tenantA, Recipient = "a2@x", StatusCode = "250", StatusMessage = "ok", TimestampUtc = stamp });
            db.DeliveryLogs.Add(new DeliveryLog { TenantId = tenantB, Recipient = "b1@y", StatusCode = "250", StatusMessage = "ok", TimestampUtc = stamp });
            await db.SaveChangesAsync();
        }

        // Aggregate with background (unscoped) semantics — sees all tenants.
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IStatisticsService>().AggregateDayAsync(date);

        // A separate per-tenant row is produced for each tenant.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var a = await db.DailyStatistics.AsNoTracking().FirstAsync(s => s.TenantId == tenantA && s.Date == date);
            var b = await db.DailyStatistics.AsNoTracking().FirstAsync(s => s.TenantId == tenantB && s.Date == date);
            Assert.AreEqual(2, a.TotalSent);
            Assert.AreEqual(1, b.TotalSent);
        }

        // The tenant query filter scopes DailyStatistics: tenant B sees only its own row.
        using (var scope = _app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ICurrentTenant>().SetTenant(tenantB);
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var rows = await db.DailyStatistics.AsNoTracking().Where(s => s.Date == date).ToListAsync();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(tenantB, rows[0].TenantId);
            Assert.AreEqual(1, rows[0].TotalSent);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task AcceptedDomain_IsGloballyUnique_AcrossTenants()
    {
        int tenantB;
        using (var scope = _app.Services.CreateScope())
            tenantB = (await scope.ServiceProvider.GetRequiredService<ITenantService>().CreateAsync("Dom Co", "dom-co")).Id;

        // The default tenant claims the domain.
        using (var scope = _app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ICurrentTenant>().SetTenant(TenantDefaults.DefaultTenantId);
            await scope.ServiceProvider.GetRequiredService<IAcceptedDomainService>().CreateAsync("shared.example");
        }

        // Tenant B sees it as taken and cannot claim it.
        using (var scope = _app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ICurrentTenant>().SetTenant(tenantB);
            var svc = scope.ServiceProvider.GetRequiredService<IAcceptedDomainService>();
            Assert.IsTrue(await svc.ExistsAsync("shared.example"), "another tenant's domain should be visible as taken");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => svc.CreateAsync("shared.example"));
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PurgeAndDelete_RemovesTenantAndAllItsData()
    {
        int tid;
        using (var scope = _app.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            tid = (await sp.GetRequiredService<ITenantService>().CreateAsync("Doomed", "doomed")).Id;

            var db = sp.GetRequiredService<RelayDbContext>();
            // A delivery log makes the tenant FK-Restrict block a plain delete.
            db.DeliveryLogs.Add(new DeliveryLog { TenantId = tid, Recipient = "r@doomed", StatusCode = "250", StatusMessage = "ok" });
            db.Users.Add(new AdminUser { UserName = "owner@doomed", Email = "owner@doomed", TenantId = tid });
            await db.SaveChangesAsync();

            await sp.GetRequiredService<IApiKeyService>().CreateAsync(tid, "k", RelayRoles.TenantAdmin, null, default);
        }

        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ITenantService>().PurgeAndDeleteAsync(tid);

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            Assert.IsFalse(await db.Tenants.AnyAsync(t => t.Id == tid), "tenant should be gone");
            Assert.IsFalse(await db.DeliveryLogs.IgnoreQueryFilters().AnyAsync(l => l.TenantId == tid), "delivery logs should be purged");
            Assert.IsFalse(await db.ApiKeys.AnyAsync(k => k.TenantId == tid), "api keys should be purged");
            Assert.IsFalse(await db.Users.AnyAsync(u => u.TenantId == tid), "admin users should be purged");
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PurgeAndDelete_RefusesDefaultTenant()
    {
        using var scope = _app.Services.CreateScope();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<ITenantService>().PurgeAndDeleteAsync(TenantDefaults.DefaultTenantId));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task TenantEgressIp_PersistsAndCacheReflects_InvalidRejected()
    {
        int tid;
        using (var scope = _app.Services.CreateScope())
            tid = (await scope.ServiceProvider.GetRequiredService<ITenantService>().CreateAsync("Egress Co", "egress-co")).Id;

        var cache = _app.Services.GetRequiredService<IRuntimeConfigCache>();
        Assert.IsNull(await cache.GetTenantEgressIpAsync(tid));

        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ITenantService>().SetEgressIpAsync(tid, "203.0.113.7");
        Assert.AreEqual("203.0.113.7", await cache.GetTenantEgressIpAsync(tid), "cache should reflect the set egress IP");

        using (var scope = _app.Services.CreateScope())
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => scope.ServiceProvider.GetRequiredService<ITenantService>().SetEgressIpAsync(tid, "not-an-ip"));

        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ITenantService>().SetEgressIpAsync(tid, "");
        Assert.IsNull(await cache.GetTenantEgressIpAsync(tid), "clearing should remove the egress IP");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SenderDomain_GetsVerificationToken_AndCanBeMarkedVerified()
    {
        int id;
        using (var scope = _app.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAcceptedSenderDomainService>();
            var created = await svc.CreateAsync("verify.example");
            id = created.Id;
            Assert.IsFalse(string.IsNullOrWhiteSpace(created.VerificationToken), "a token should be generated on create");
            Assert.IsNull(created.VerifiedUtc, "a new domain starts unverified");
        }

        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAcceptedSenderDomainService>().MarkVerifiedAsync(id);

        using (var scope = _app.Services.CreateScope())
        {
            var domain = (await scope.ServiceProvider.GetRequiredService<IAcceptedSenderDomainService>().GetAllAsync())
                .First(d => d.Id == id);
            Assert.IsNotNull(domain.VerifiedUtc, "MarkVerified should set VerifiedUtc");
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PortalSettings_SignupToggle_DefaultsOff_AndPersists()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IPortalSettingsService>();
            Assert.IsFalse((await svc.GetAsync()).SelfServiceSignupEnabled, "signup should default to off");
            await svc.SetSelfServiceSignupEnabledAsync(true);
        }

        using (var scope = _app.Services.CreateScope())
            Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<IPortalSettingsService>().GetAsync()).SelfServiceSignupEnabled,
                "the enabled toggle should persist");

        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPortalSettingsService>().SetSelfServiceSignupEnabledAsync(false);

        using (var scope = _app.Services.CreateScope())
            Assert.IsFalse((await scope.ServiceProvider.GetRequiredService<IPortalSettingsService>().GetAsync()).SelfServiceSignupEnabled,
                "the disabled toggle should persist");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PortalSettings_SignupFromAddress_DefaultsNull_PersistsAndClears()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IPortalSettingsService>();
            Assert.IsNull((await svc.GetAsync()).SignupFromAddress, "from-address should default to null");
            await svc.SetSignupFromAddressAsync("noreply@test.example");
        }

        using (var scope = _app.Services.CreateScope())
            Assert.AreEqual("noreply@test.example",
                (await scope.ServiceProvider.GetRequiredService<IPortalSettingsService>().GetAsync()).SignupFromAddress);

        // Blank clears it back to null (so the appsettings fallback applies).
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPortalSettingsService>().SetSignupFromAddressAsync("   ");
        using (var scope = _app.Services.CreateScope())
            Assert.IsNull((await scope.ServiceProvider.GetRequiredService<IPortalSettingsService>().GetAsync()).SignupFromAddress);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PortalSettings_SignupThrottle_PersistsAndClamps()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IPortalSettingsService>();
            Assert.AreEqual(5, (await svc.GetAsync()).SignupMaxAttemptsPerIpPerHour, "default seed is 5");
            await svc.SetSignupMaxAttemptsPerIpPerHourAsync(12);
        }
        using (var scope = _app.Services.CreateScope())
            Assert.AreEqual(12, (await scope.ServiceProvider.GetRequiredService<IPortalSettingsService>().GetAsync()).SignupMaxAttemptsPerIpPerHour);

        // Negative clamps to 0 (disabled).
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPortalSettingsService>().SetSignupMaxAttemptsPerIpPerHourAsync(-3);
        using (var scope = _app.Services.CreateScope())
            Assert.AreEqual(0, (await scope.ServiceProvider.GetRequiredService<IPortalSettingsService>().GetAsync()).SignupMaxAttemptsPerIpPerHour);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task EmailAuthSettings_UpdatePersists_AndCacheReflects()
    {
        var cache = _app.Services.GetRequiredService<IRuntimeConfigCache>();
        var before = await cache.GetEmailAuthSettingsAsync();
        Assert.IsFalse(before.SpfEnabled, "SPF defaults off");
        Assert.AreEqual(WinSmtpRelay.Core.Configuration.EnforcementMode.LogOnly, before.Enforcement);

        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IEmailAuthSettingsService>()
                .UpdateAsync(spfEnabled: true, dmarcEnabled: true, WinSmtpRelay.Core.Configuration.EnforcementMode.Reject);

        var after = await cache.GetEmailAuthSettingsAsync();
        Assert.IsTrue(after.SpfEnabled, "cache should reflect the persisted SPF change after invalidation");
        Assert.IsTrue(after.DmarcEnabled);
        Assert.AreEqual(WinSmtpRelay.Core.Configuration.EnforcementMode.Reject, after.Enforcement);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task BackupMxSettings_UpdatePersists_CacheReflects_AndParsesDomains()
    {
        var cache = _app.Services.GetRequiredService<IRuntimeConfigCache>();
        Assert.IsFalse((await cache.GetBackupMxSettingsAsync()).Enabled, "backup MX defaults off");

        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBackupMxSettingsService>()
                .UpdateAsync(enabled: true, domains: "example.com; example.net", retryIntervalMinutes: 10, maxHoldHours: 72);

        var after = await cache.GetBackupMxSettingsAsync();
        Assert.IsTrue(after.Enabled, "cache should reflect the enable after invalidation");
        Assert.AreEqual(10, after.RetryIntervalMinutes);
        Assert.AreEqual(72, after.MaxHoldHours);
        CollectionAssert.AreEquivalent(new[] { "example.com", "example.net" }, after.DomainList.ToArray());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task StatisticsRetentionSettings_UpdatePersists_AndClamps()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IStatisticsRetentionSettingsService>();
            var d = await svc.GetAsync();
            Assert.AreEqual(90, d.RetentionDays, "default retention is 90 days");
            Assert.AreEqual("00:00", d.AggregationTimeUtc);
            await svc.UpdateAsync(retentionDays: 30, aggregationTimeUtc: "02:30");
        }

        using (var scope = _app.Services.CreateScope())
        {
            var d = await scope.ServiceProvider.GetRequiredService<IStatisticsRetentionSettingsService>().GetAsync();
            Assert.AreEqual(30, d.RetentionDays);
            Assert.AreEqual("02:30", d.AggregationTimeUtc);
        }

        // Retention clamps to >= 1.
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IStatisticsRetentionSettingsService>().UpdateAsync(0, "00:00");
        using (var scope = _app.Services.CreateScope())
            Assert.AreEqual(1, (await scope.ServiceProvider.GetRequiredService<IStatisticsRetentionSettingsService>().GetAsync()).RetentionDays);
    }

    private record HealthResponse(string Status);
}

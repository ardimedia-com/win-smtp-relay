using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.AdminApi;
using WinSmtpRelay.AdminApi.Auth;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

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

    private record HealthResponse(string Status);
}

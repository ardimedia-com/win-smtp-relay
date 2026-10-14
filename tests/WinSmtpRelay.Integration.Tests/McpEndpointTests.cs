using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.AdminApi;
using WinSmtpRelay.AdminApi.Auth;
using WinSmtpRelay.AdminApi.Mcp;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Integration.Tests;

/// <summary>
/// Smoke-tests the hosted MCP endpoint over raw JSON-RPC (stateless streamable HTTP): authentication,
/// tool discovery, a read tool call, and the per-tool scope enforcement for write tools.
/// </summary>
[TestClass]
public class McpEndpointTests
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;       // HostAdmin key with troubleshooting scopes
    private HttpClient _viewerClient = null!; // TenantViewer key, scope-less (= read-only)
    private HttpClient _anonClient = null!;
    private string _dbPath = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mcp_test_{Guid.NewGuid()}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRelayStorage($"Data Source={_dbPath}");
        builder.Services.AddRelaySecurity(); // IDnsSetupService for set_dkim_enabled
        builder.Services.AddRelayAdminAuth();
        builder.Services.AddRelayMcp();

        _app = builder.Build();

        string hostKey, viewerKey;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            await db.Database.MigrateAsync();

            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AdminRole>>();
            foreach (var role in RelayRoles.All)
                if (!await roleManager.RoleExistsAsync(role))
                    await roleManager.CreateAsync(new AdminRole(role));

            var keys = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
            // The recommended troubleshooting-key shape from the MCP setup page.
            (_, hostKey) = await keys.CreateAsync(null, "mcp-host", RelayRoles.HostAdmin,
                "diag:read messages:read queue:write config:write", null, default);
            (_, viewerKey) = await keys.CreateAsync(TenantDefaults.DefaultTenantId, "mcp-viewer", RelayRoles.TenantViewer, null, null, default);
        }

        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseRelayTenantContext();
        _app.MapRelayMcp();

        await _app.StartAsync();

        var address = _app.Urls.First();
        _client = NewMcpClient(address, hostKey);
        _viewerClient = NewMcpClient(address, viewerKey);
        _anonClient = NewMcpClient(address, null);
    }

    private static HttpClient NewMcpClient(string address, string? key)
    {
        var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (key is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        _viewerClient.Dispose();
        _anonClient.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static StringContent Rpc(string method, object? @params = null) => new(
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = @params ?? new { } }),
        Encoding.UTF8, "application/json");

    /// <summary>Extracts the JSON-RPC payload whether the server answered plain JSON or SSE-framed.</summary>
    private static async Task<JsonElement> ReadRpcAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var dataLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("data: ", StringComparison.Ordinal));
        var json = dataLine is not null ? dataLine["data: ".Length..] : body;
        return JsonDocument.Parse(json).RootElement;
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_Unauthenticated_Returns401()
    {
        var response = await _anonClient.PostAsync("/mcp", Rpc("tools/list"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_ToolsList_ExposesDiagnoseAndFixTools()
    {
        var response = await _client.PostAsync("/mcp", Rpc("tools/list"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var rpc = await ReadRpcAsync(response);
        var names = rpc.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();

        CollectionAssert.IsSubsetOf(new[]
        {
            "get_server_status", "list_queue_messages", "get_message", "list_delivery_logs",
            "list_rejections", "get_health_check", "query_audit", "get_relay_config",
            "list_suppressions", "add_accepted_sender_domain", "remove_accepted_sender_domain", "add_accepted_domain",
            "add_ip_allow_rule", "add_suppression", "remove_suppression", "requeue_message",
            "get_sending_identity", "set_sending_identity",
            "list_dkim_domains", "generate_dkim_key", "set_dkim_enabled",
        }, names);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_GenerateDkimKey_ReturnsDnsRecord_NeverThePrivateKey_AndStartsDisabled()
    {
        await _client.PostAsync("/mcp", Rpc("tools/call",
            new { name = "add_accepted_sender_domain", arguments = new { domain = "signed.example" } }));

        var response = await _client.PostAsync("/mcp", Rpc("tools/call",
            new { name = "generate_dkim_key", arguments = new { domain = "signed.example", selector = "mcp1" } }));
        var result = (await ReadRpcAsync(response)).GetProperty("result");
        Assert.IsFalse(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), $"expected success, got: {result}");

        var text = result.ToString();
        StringAssert.Contains(text, "mcp1._domainkey.signed.example");
        StringAssert.Contains(text, "v=DKIM1; k=rsa; p=");
        Assert.IsFalse(text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase), "the private key must never leave the server");
        // The tool result is JSON carried as the content text.
        var payload = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;
        Assert.IsFalse(payload.GetProperty("isEnabled").GetBoolean(), $"key must start disabled: {text}");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_SetSendingIdentity_WithViewerKey_IsToolError()
    {
        var response = await _viewerClient.PostAsync("/mcp", Rpc("tools/call",
            new { name = "set_sending_identity", arguments = new { sendingIpAddresses = new[] { "203.0.113.10" } } }));
        var result = (await ReadRpcAsync(response)).GetProperty("result");
        Assert.IsTrue(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
            $"a viewer key must not change the sending identity: {result}");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_GetServerStatus_WorksForScopelessReadOnlyKey()
    {
        var response = await _viewerClient.PostAsync("/mcp", Rpc("tools/call",
            new { name = "get_server_status", arguments = new { } }));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var rpc = await ReadRpcAsync(response);
        var result = rpc.GetProperty("result");
        Assert.IsFalse(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
            $"expected success, got: {result}");
        StringAssert.Contains(result.ToString(), "queueDepth", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_WriteTool_WithScopelessKey_IsToolError()
    {
        var response = await _viewerClient.PostAsync("/mcp", Rpc("tools/call",
            new { name = "add_accepted_sender_domain", arguments = new { domain = "blocked.example" } }));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var rpc = await ReadRpcAsync(response);
        var result = rpc.GetProperty("result");
        Assert.IsTrue(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
            $"a viewer key must not create sender domains: {result}");
        // The denial is thrown as McpException, so its actionable message reaches the assistant
        // (a generic exception would be replaced with a generic message and logged at Error).
        StringAssert.Contains(result.ToString(), "role", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_WriteTool_WithScopedHostKey_Succeeds_AndIsAudited()
    {
        var response = await _client.PostAsync("/mcp", Rpc("tools/call",
            new { name = "add_accepted_sender_domain", arguments = new { domain = "fixed.example" } }));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var rpc = await ReadRpcAsync(response);
        var result = rpc.GetProperty("result");
        Assert.IsFalse(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
            $"expected success, got: {result}");

        // The mutation went through the audited service with the API-key actor.
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var audit = await db.AdminAuditEvents.AsNoTracking()
            .Where(e => e.Action == AdminAuditActions.SenderDomainCreated)
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        Assert.IsNotNull(audit, "the MCP write must leave an audit row");
        Assert.IsNotNull(audit.ActorApiKeyId, "the audit row must name the API key as actor");
        Assert.AreEqual("mcp-host", audit.ActorEmail);
    }

    /// <summary>Calls one tool and returns the JSON-RPC result (content + isError).</summary>
    private static async Task<JsonElement> CallToolAsync(HttpClient client, string name, object arguments)
    {
        var response = await client.PostAsync("/mcp", Rpc("tools/call", new { name, arguments }));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return (await ReadRpcAsync(response)).GetProperty("result");
    }

    private static bool IsToolError(JsonElement result) =>
        result.TryGetProperty("isError", out var isError) && isError.GetBoolean();

    private async Task<bool> SenderDomainExistsAsync(string domain)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.AcceptedSenderDomains.IgnoreQueryFilters().AsNoTracking().AnyAsync(d => d.Domain == domain);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_RemoveAcceptedSenderDomain_ByDomain_RemovesIt_AndIsAudited()
    {
        await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "keep.example" });
        await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "gone.example" });

        // Input is trimmed and lower-cased like add_accepted_sender_domain.
        var result = await CallToolAsync(_client, "remove_accepted_sender_domain", new { domain = "  Gone.Example " });
        Assert.IsFalse(IsToolError(result), $"expected success, got: {result}");
        StringAssert.Contains(result.ToString(), "gone.example");
        Assert.IsFalse(await SenderDomainExistsAsync("gone.example"));
        Assert.IsTrue(await SenderDomainExistsAsync("keep.example"));

        // The removal went through the audited service with the API-key actor.
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var audit = await db.AdminAuditEvents.AsNoTracking()
            .Where(e => e.Action == AdminAuditActions.SenderDomainDeleted)
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        Assert.IsNotNull(audit, "the removal must leave an audit row");
        Assert.AreEqual("gone.example", audit.Detail);
        Assert.IsNotNull(audit.ActorApiKeyId, "the audit row must name the API key as actor");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_RemoveAcceptedSenderDomain_ById_RemovesIt()
    {
        await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "keep.example" });
        await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "byid.example" });
        int id;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            id = await db.AcceptedSenderDomains.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Domain == "byid.example").Select(d => d.Id).SingleAsync();
        }

        var result = await CallToolAsync(_client, "remove_accepted_sender_domain", new { id });
        Assert.IsFalse(IsToolError(result), $"expected success, got: {result}");
        Assert.IsFalse(await SenderDomainExistsAsync("byid.example"));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_RemoveAcceptedSenderDomain_Unknown_IsToolError()
    {
        await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "keep.example" });

        var result = await CallToolAsync(_client, "remove_accepted_sender_domain", new { domain = "unknown.example" });
        Assert.IsTrue(IsToolError(result), $"an unknown domain must be a tool error: {result}");
        StringAssert.Contains(result.ToString(), "not an accepted sender domain");

        var byId = await CallToolAsync(_client, "remove_accepted_sender_domain", new { id = 999999 });
        Assert.IsTrue(IsToolError(byId), $"an unknown id must be a tool error: {byId}");
        StringAssert.Contains(byId.ToString(), "was not found");

        var neither = await CallToolAsync(_client, "remove_accepted_sender_domain", new { });
        Assert.IsTrue(IsToolError(neither), $"a call without domain or id must be a tool error: {neither}");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_RemoveAcceptedSenderDomain_LastDomain_IsRefused()
    {
        await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "only.example" });

        var result = await CallToolAsync(_client, "remove_accepted_sender_domain", new { domain = "only.example" });
        Assert.IsTrue(IsToolError(result), $"removing the last sender domain must be refused: {result}");
        StringAssert.Contains(result.ToString(), "last accepted sender domain");
        Assert.IsTrue(await SenderDomainExistsAsync("only.example"));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_RemoveAcceptedSenderDomain_WithPendingMessages_IsRefused()
    {
        await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "keep.example" });
        await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "busy.example" });
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            db.QueuedMessages.Add(new QueuedMessage
            {
                MessageId = "<pending@busy.example>", Sender = "Printer@Busy.Example",
                Recipients = "someone@elsewhere.example", RawMessage = [], Status = MessageStatus.Queued,
            });
            await db.SaveChangesAsync();
        }

        var result = await CallToolAsync(_client, "remove_accepted_sender_domain", new { domain = "busy.example" });
        Assert.IsTrue(IsToolError(result), $"pending mail from the domain must block the removal: {result}");
        StringAssert.Contains(result.ToString(), "waiting for delivery");
        Assert.IsTrue(await SenderDomainExistsAsync("busy.example"));

        // Once the message is delivered the removal goes through.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            await db.QueuedMessages.IgnoreQueryFilters()
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Delivered));
        }
        var retry = await CallToolAsync(_client, "remove_accepted_sender_domain", new { domain = "busy.example" });
        Assert.IsFalse(IsToolError(retry), $"expected success after delivery, got: {retry}");
        Assert.IsFalse(await SenderDomainExistsAsync("busy.example"));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_RemoveAcceptedSenderDomain_WithScopelessKey_IsToolError()
    {
        await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "keep.example" });
        await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "protected.example" });

        var result = await CallToolAsync(_viewerClient, "remove_accepted_sender_domain", new { domain = "protected.example" });
        Assert.IsTrue(IsToolError(result), $"a viewer key must not remove sender domains: {result}");
        StringAssert.Contains(result.ToString(), "role", StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(await SenderDomainExistsAsync("protected.example"));
    }

    // ---- T9: a host-level key names the tenant of a tenant write ----

    private async Task<int> AddTenantAsync(string slug)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var tenant = new Tenant { Name = slug, Slug = slug };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<int?> SenderDomainTenantAsync(string domain)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.AcceptedSenderDomains.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Domain == domain).Select(d => (int?)d.TenantId).FirstOrDefaultAsync();
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_HostKey_SingleTenant_WriteLandsInThatTenant()
    {
        var result = await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "single.example" });

        Assert.IsFalse(IsToolError(result), $"expected success, got: {result}");
        Assert.AreEqual(TenantDefaults.DefaultTenantId, await SenderDomainTenantAsync("single.example"));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_HostKey_SeveralTenants_WithoutTenant_IsRefused_AndNamesTheTenants()
    {
        await AddTenantAsync("acme");

        var result = await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "nowhere.example" });

        Assert.IsTrue(IsToolError(result), $"a tenant write without a tenant must be refused: {result}");
        StringAssert.Contains(result.ToString(), "acme");
        Assert.IsFalse(await SenderDomainExistsAsync("nowhere.example"), "nothing may land in the Default tenant");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_HostKey_SeveralTenants_WithTenantArgument_LandsInThatTenant()
    {
        var acme = await AddTenantAsync("acme");

        var bySlug = await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "acme.example", tenant = "acme" });
        var byId = await CallToolAsync(_client, "add_ip_allow_rule", new { network = "192.0.2.10/32", tenant = acme.ToString() });

        Assert.IsFalse(IsToolError(bySlug), $"expected success, got: {bySlug}");
        Assert.IsFalse(IsToolError(byId), $"expected success, got: {byId}");
        Assert.AreEqual(acme, await SenderDomainTenantAsync("acme.example"));
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var rule = await db.IpAccessRules.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Network == "192.0.2.10/32");
        Assert.AreEqual(acme, rule.TenantId);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_HostKey_TenantHeader_PinsTheWholeRequest()
    {
        var acme = await AddTenantAsync("acme");
        _client.DefaultRequestHeaders.Add(RelayTenantContextMiddleware.TenantHeader, "acme");

        var result = await CallToolAsync(_client, "add_accepted_sender_domain", new { domain = "header.example" });

        Assert.IsFalse(IsToolError(result), $"expected success, got: {result}");
        Assert.AreEqual(acme, await SenderDomainTenantAsync("header.example"));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_TenantHeader_UnknownTenant_IsBadRequest()
    {
        _client.DefaultRequestHeaders.Add(RelayTenantContextMiddleware.TenantHeader, "no-such-tenant");

        var response = await _client.PostAsync("/mcp", Rpc("tools/list"));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Mcp_TenantKey_NamingAnotherTenant_IsRefused()
    {
        await AddTenantAsync("acme");

        var result = await CallToolAsync(_viewerClient, "list_dkim_domains", new { tenant = "acme" });

        Assert.IsTrue(IsToolError(result), $"a tenant key must not reach another tenant: {result}");
        StringAssert.Contains(result.ToString(), "bound to tenant");
    }
}

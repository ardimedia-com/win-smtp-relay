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
            "list_suppressions", "add_accepted_sender_domain", "add_accepted_domain",
            "add_ip_allow_rule", "add_suppression", "remove_suppression", "requeue_message",
        }, names);
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
}

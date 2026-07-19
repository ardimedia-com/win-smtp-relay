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
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Integration.Tests;

/// <summary>
/// Exercises the diagnostics surface added for automation/MCP: audit query, health-check snapshots,
/// suppressions, and rejections — including their role policies and API-key scope behaviour.
/// </summary>
[TestClass]
public class DiagnosticsApiTests
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;       // HostAdmin key with every write scope
    private HttpClient _viewerClient = null!; // TenantViewer key, scope-less (= read-only)
    private string _dbPath = null!;

    private static readonly string AllWriteScopes =
        string.Join(' ', ApiKeyScopes.Areas.Select(ApiKeyScopes.Write).Append(ApiKeyScopes.MessagesBody));

    [TestInitialize]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"diagapi_test_{Guid.NewGuid()}.db");

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

            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AdminRole>>();
            foreach (var role in RelayRoles.All)
                if (!await roleManager.RoleExistsAsync(role))
                    await roleManager.CreateAsync(new AdminRole(role));

            var keys = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
            (_, hostKey) = await keys.CreateAsync(null, "diag-host", RelayRoles.HostAdmin, AllWriteScopes, null, default);
            (_, viewerKey) = await keys.CreateAsync(TenantDefaults.DefaultTenantId, "diag-viewer", RelayRoles.TenantViewer, null, null, default);
        }

        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseRelayTenantContext();
        _app.MapAdminApi();

        await _app.StartAsync();

        var address = _app.Urls.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
        _client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, hostKey);

        _viewerClient = new HttpClient { BaseAddress = new Uri(address) };
        _viewerClient.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, viewerKey);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        _viewerClient.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ---- Audit ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Audit_MutationViaApiKey_IsQueryableAndAttributed()
    {
        // Any audited mutation will do — creating a relay user is audited at the service.
        var create = await _client.PostAsJsonAsync("/api/users", new CreateUserRequest("audituser", "P@ssw0rd!"));
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);

        var response = await _client.GetAsync($"/api/audit?action={AdminAuditActions.RelayUserCreated}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuditQueryResponse>();
        Assert.IsNotNull(body);
        Assert.IsTrue(body.Total >= 1);
        var row = body.Events.First(e => e.Action == AdminAuditActions.RelayUserCreated);
        Assert.IsNotNull(row.ActorApiKeyId, "an API-key mutation must be attributed to the key");
        Assert.AreEqual("diag-host", row.ActorEmail, "the audit row carries the key's name");
        Assert.IsNull(row.ActorUserId, "an API-key actor is not an admin user");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Audit_TenantViewerKey_IsForbidden()
    {
        // The audit endpoint is HostAdmin-only, mirroring the UI page.
        var response = await _viewerClient.GetAsync("/api/audit");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Health-check snapshots ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task HealthChecks_Latest_404BeforeFirstRun_ThenReturnsFindings()
    {
        var empty = await _client.GetAsync("/api/health/checks/latest");
        Assert.AreEqual(HttpStatusCode.NotFound, empty.StatusCode);

        using (var scope = _app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IHealthCheckSnapshotService>().SaveAsync(new HealthCheckSnapshot
            {
                DurationMs = 1200,
                ErrorCount = 1,
                OkCount = 6,
                Findings =
                [
                    new HealthCheckFinding
                    {
                        Category = "delivery", Code = "outbound-port-25", Severity = HealthSeverity.Error,
                        Title = "Outbound port 25 blocked", Detail = "TCP connect to port 25 timed out.",
                        Hint = "Use a smart host or ask the ISP to unblock port 25."
                    }
                ]
            });
        }

        var response = await _client.GetAsync("/api/health/checks/latest");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<HealthSnapshotDetail>();
        Assert.IsNotNull(snapshot);
        Assert.AreEqual("Error", snapshot.OverallSeverity);
        Assert.AreEqual(1, snapshot.Findings.Count);
        Assert.AreEqual("outbound-port-25", snapshot.Findings[0].Code);

        var history = await _client.GetFromJsonAsync<HealthSnapshotSummary[]>("/api/health/checks/history");
        Assert.AreEqual(1, history!.Length);
    }

    // ---- Suppressions ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Suppressions_AddListRemove_RoundTrip()
    {
        // Host scope must name the tenant explicitly.
        var missingTenant = await _client.PostAsJsonAsync("/api/suppressions",
            new CreateSuppressionRequest("Dead@Example.com"));
        Assert.AreEqual(HttpStatusCode.BadRequest, missingTenant.StatusCode);

        var add = await _client.PostAsJsonAsync("/api/suppressions",
            new CreateSuppressionRequest("Dead@Example.com", "manual test", TenantDefaults.DefaultTenantId));
        Assert.AreEqual(HttpStatusCode.OK, add.StatusCode);

        var list = await _client.GetFromJsonAsync<SuppressionSummary[]>("/api/suppressions");
        Assert.AreEqual(1, list!.Length);
        Assert.AreEqual("dead@example.com", list[0].Address, "addresses are normalised to lower-case");
        Assert.AreEqual(nameof(SuppressionReason.Manual), list[0].Reason);

        var remove = await _client.DeleteAsync($"/api/suppressions/{list[0].Id}");
        Assert.AreEqual(HttpStatusCode.OK, remove.StatusCode);

        list = await _client.GetFromJsonAsync<SuppressionSummary[]>("/api/suppressions");
        Assert.AreEqual(0, list!.Length);

        // Both mutations left audit rows attributed to the key.
        var audit = await _client.GetFromJsonAsync<AuditQueryResponse>(
            $"/api/audit?search={Uri.EscapeDataString("dead@example.com")}");
        CollectionAssert.AreEquivalent(
            new[] { AdminAuditActions.SuppressionAdded, AdminAuditActions.SuppressionRemoved },
            audit!.Events.Select(e => e.Action).ToArray());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Suppressions_ScopelessViewerKey_CannotWrite()
    {
        // The subgroup requires AdminFull; a TenantViewer fails role-first. (A scope-less TenantAdmin
        // key would instead fail on the missing config:write scope — same outcome, read-only.)
        var response = await _viewerClient.PostAsJsonAsync("/api/suppressions",
            new CreateSuppressionRequest("x@example.com", null, TenantDefaults.DefaultTenantId));
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Rejections ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Rejections_ListsRows_AndHidesIgnoredByDefault()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            db.RejectedSubmissions.AddRange(
                new RejectedSubmission
                {
                    TenantId = TenantDefaults.DefaultTenantId,
                    ClientIp = "10.0.0.5",
                    Reason = RejectReason.SenderDomainNotAccepted,
                    ReplyCode = 550,
                    SenderDomain = "acme.example",
                    IsTrustedSource = true,
                    Count = 12,
                    FirstSeenUtc = DateTimeOffset.UtcNow.AddHours(-6),
                    LastSeenUtc = DateTimeOffset.UtcNow
                },
                new RejectedSubmission
                {
                    ClientIp = "203.0.113.9",
                    Reason = RejectReason.OpenRelayDenied,
                    ReplyCode = 550,
                    IsTrustedSource = false,
                    Count = 400,
                    FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-2),
                    LastSeenUtc = DateTimeOffset.UtcNow,
                    IgnoredUtc = DateTimeOffset.UtcNow.AddHours(-1)
                });
            await db.SaveChangesAsync();
        }

        // Default hides ignored rows; the viewer key (scope-less = read-only) may read this.
        var visible = await _viewerClient.GetFromJsonAsync<RejectionSummary[]>("/api/rejections");
        Assert.AreEqual(1, visible!.Length);
        Assert.AreEqual(nameof(RejectReason.SenderDomainNotAccepted), visible[0].Reason);
        Assert.IsFalse(string.IsNullOrEmpty(visible[0].ReasonDescription));

        var all = await _client.GetFromJsonAsync<RejectionSummary[]>("/api/rejections?includeIgnored=true");
        Assert.AreEqual(2, all!.Length);
    }
}

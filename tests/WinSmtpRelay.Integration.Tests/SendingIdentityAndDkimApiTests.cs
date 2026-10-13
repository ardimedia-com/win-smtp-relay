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
using WinSmtpRelay.Security;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Integration.Tests;

/// <summary>
/// The sending identity (host-only) and the safe DKIM flow (tenant-usable) over REST: role and scope checks,
/// validation, disabled-by-default keys, the publish-before-enable gate, and the audit trail.
/// </summary>
[TestClass]
public class SendingIdentityAndDkimApiTests
{
    private WebApplication _app = null!;
    private HttpClient _host = null!;         // HostAdmin key, every write scope
    private HttpClient _tenantAdmin = null!;  // TenantAdmin key of the Default tenant, config:write
    private HttpClient _hostDiagOnly = null!; // HostAdmin key, diag:read only
    private string _dbPath = null!;

    private static readonly string AllWriteScopes =
        string.Join(' ', ApiKeyScopes.Areas.Select(ApiKeyScopes.Write));

    [TestInitialize]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"identity_dkim_test_{Guid.NewGuid()}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRelayStorage($"Data Source={_dbPath}");
        builder.Services.AddRelaySecurity();
        builder.Services.AddRelayAdminAuth();

        _app = builder.Build();

        string hostKey, tenantKey, diagKey;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            await db.Database.MigrateAsync();

            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AdminRole>>();
            foreach (var role in RelayRoles.All)
                if (!await roleManager.RoleExistsAsync(role))
                    await roleManager.CreateAsync(new AdminRole(role));

            var keys = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
            (_, hostKey) = await keys.CreateAsync(null, "host-all", RelayRoles.HostAdmin, AllWriteScopes, null, default);
            (_, tenantKey) = await keys.CreateAsync(TenantDefaults.DefaultTenantId, "tenant-config", RelayRoles.TenantAdmin,
                $"{ApiKeyScopes.Write(ApiKeyScopes.Config)} {ApiKeyScopes.Read(ApiKeyScopes.Diag)}", null, default);
            (_, diagKey) = await keys.CreateAsync(null, "host-diag", RelayRoles.HostAdmin, ApiKeyScopes.Read(ApiKeyScopes.Diag), null, default);
        }

        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseRelayTenantContext();
        _app.MapAdminApi();

        await _app.StartAsync();

        var address = new Uri(_app.Urls.First());
        _host = Client(address, hostKey);
        _tenantAdmin = Client(address, tenantKey);
        _hostDiagOnly = Client(address, diagKey);
    }

    private static HttpClient Client(Uri address, string key)
    {
        var client = new HttpClient { BaseAddress = address };
        client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, key);
        return client;
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _host.Dispose();
        _tenantAdmin.Dispose();
        _hostDiagOnly.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ---- Sending identity (T8) ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SendingIdentity_HostKey_SetsAndReads_AndIsAudited()
    {
        var put = await _host.PutAsJsonAsync("/api/settings/sending-identity",
            new UpdateSendingIdentityRequest("smtp.example.com", ["203.0.113.10"]));
        Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);

        var identity = await _host.GetFromJsonAsync<SendingIdentityResponse>("/api/settings/sending-identity");
        Assert.AreEqual("smtp.example.com", identity!.PublicHostname);
        CollectionAssert.AreEqual(new[] { "203.0.113.10" }, identity.SendingIpAddresses.ToArray());
        Assert.AreEqual(0, identity.PrivateSendingIpAddresses.Count);

        // A partial update leaves the other field alone.
        await _host.PutAsJsonAsync("/api/settings/sending-identity", new UpdateSendingIdentityRequest(PublicHostname: "relay.example.com"));
        identity = await _host.GetFromJsonAsync<SendingIdentityResponse>("/api/settings/sending-identity");
        Assert.AreEqual("relay.example.com", identity!.PublicHostname);
        CollectionAssert.AreEqual(new[] { "203.0.113.10" }, identity.SendingIpAddresses.ToArray());

        var audit = await _host.GetFromJsonAsync<AuditQueryResponse>($"/api/audit?action={AdminAuditActions.DnsSettingsUpdated}");
        Assert.AreEqual(2, audit!.Total);
        Assert.IsTrue(audit.Events.All(e => e.ActorApiKeyId is not null), "changes must be attributed to the key");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SendingIdentity_InvalidIp_IsBadRequest()
    {
        var put = await _host.PutAsJsonAsync("/api/settings/sending-identity",
            new UpdateSendingIdentityRequest(SendingIpAddresses: ["smtp.example.com"]));
        Assert.AreEqual(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SendingIdentity_PrivateIp_IsAccepted_ButReportedAsPrivate()
    {
        var put = await _host.PutAsJsonAsync("/api/settings/sending-identity",
            new UpdateSendingIdentityRequest(SendingIpAddresses: ["10.1.1.122", "203.0.113.10"]));
        Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);

        var identity = await put.Content.ReadFromJsonAsync<SendingIdentityResponse>();
        CollectionAssert.AreEqual(new[] { "10.1.1.122" }, identity!.PrivateSendingIpAddresses.ToArray());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SendingIdentity_TenantKey_IsForbidden_AndDiagOnlyHostKey_CannotRead()
    {
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _tenantAdmin.GetAsync("/api/settings/sending-identity")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _hostDiagOnly.GetAsync("/api/settings/sending-identity")).StatusCode);
    }

    // ---- DKIM keys (T7) ----

    private async Task AcceptSenderDomainAsync(HttpClient client, string domain)
    {
        var response = await client.PostAsJsonAsync("/api/domains/accepted-sender", new CreateAcceptedSenderDomainRequest(domain));
        Assert.IsTrue(response.IsSuccessStatusCode, $"accept {domain}: {response.StatusCode}");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DkimKey_TenantKey_GeneratesDisabledKey_WithDnsRecord_NoPrivateKey()
    {
        await AcceptSenderDomainAsync(_tenantAdmin, "acme.example");

        var response = await _tenantAdmin.PostAsJsonAsync("/api/dkim/keys", new GenerateDkimKeyRequest("acme.example", "sel1"));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(body.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase), "the private key must never leave the server");

        var key = System.Text.Json.JsonSerializer.Deserialize<DkimKeyInfo>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.IsFalse(key.IsEnabled, "a new key starts disabled");
        Assert.AreEqual("sel1._domainkey.acme.example", key.DnsName);
        StringAssert.StartsWith(key.DnsTxtValue, "v=DKIM1; k=rsa; p=");
        Assert.AreEqual(TenantDefaults.DefaultTenantId, key.TenantId);

        var list = await _tenantAdmin.GetFromJsonAsync<DkimKeyInfo[]>("/api/dkim/keys");
        Assert.AreEqual(1, list!.Length);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DkimKey_DomainNotAccepted_IsBadRequest()
    {
        var response = await _tenantAdmin.PostAsJsonAsync("/api/dkim/keys", new GenerateDkimKeyRequest("unknown.example"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DkimKey_ExistingKey_IsReplacedOnlyWithReplace()
    {
        await AcceptSenderDomainAsync(_tenantAdmin, "acme.example");
        Assert.AreEqual(HttpStatusCode.Created,
            (await _tenantAdmin.PostAsJsonAsync("/api/dkim/keys", new GenerateDkimKeyRequest("acme.example", "sel1"))).StatusCode);

        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _tenantAdmin.PostAsJsonAsync("/api/dkim/keys", new GenerateDkimKeyRequest("acme.example", "sel2"))).StatusCode);

        var replaced = await _tenantAdmin.PostAsJsonAsync("/api/dkim/keys", new GenerateDkimKeyRequest("acme.example", "sel2", Replace: true));
        Assert.AreEqual(HttpStatusCode.Created, replaced.StatusCode);
        var key = await replaced.Content.ReadFromJsonAsync<DkimKeyInfo>();
        Assert.AreEqual("sel2", key!.Selector);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DkimKey_EnableWithoutPublishedRecord_IsRefused_DisableIsAllowed()
    {
        // .invalid never resolves (RFC 2606), so the record is "not published" with or without internet.
        await AcceptSenderDomainAsync(_tenantAdmin, "acme.invalid");
        await _tenantAdmin.PostAsJsonAsync("/api/dkim/keys", new GenerateDkimKeyRequest("acme.invalid", "sel1"));

        var enable = await _tenantAdmin.PutAsJsonAsync("/api/dkim/keys/acme.invalid/enabled", new SetDkimEnabledRequest(true));
        Assert.AreEqual(HttpStatusCode.Conflict, enable.StatusCode);

        var disable = await _tenantAdmin.PutAsJsonAsync("/api/dkim/keys/acme.invalid/enabled", new SetDkimEnabledRequest(false));
        Assert.AreEqual(HttpStatusCode.OK, disable.StatusCode);

        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _tenantAdmin.PutAsJsonAsync("/api/dkim/keys/other.invalid/enabled", new SetDkimEnabledRequest(false))).StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DkimKey_RawDomainEndpoints_StayHostOnly()
    {
        // The raw endpoints accept PrivateKeyPath (an arbitrary host path) — tenant keys must not reach them.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _tenantAdmin.GetAsync("/api/dkim/domains")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _host.GetAsync("/api/dkim/domains")).StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DkimKey_Generation_IsAudited()
    {
        await AcceptSenderDomainAsync(_tenantAdmin, "acme.example");
        await _tenantAdmin.PostAsJsonAsync("/api/dkim/keys", new GenerateDkimKeyRequest("acme.example", "sel1"));

        var audit = await _host.GetFromJsonAsync<AuditQueryResponse>($"/api/audit?action={AdminAuditActions.DkimDomainCreated}");
        Assert.AreEqual(1, audit!.Total);
        Assert.AreEqual("tenant-config", audit.Events[0].ActorEmail);
        Assert.IsFalse(audit.Events[0].Detail?.Contains("PRIVATE", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}

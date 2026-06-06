using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.AdminApi;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        // Baseline: every /api endpoint requires an authenticated admin (any role).
        var group = endpoints.MapGroup("/api").RequireAuthorization(AuthorizationPolicies.AdminView);

        // Elevate every mutating endpoint (non-GET) to AdminFull, so read-only
        // viewers cannot create/update/delete. /api/health opts out via AllowAnonymous.
        ((IEndpointConventionBuilder)group).Add(builder =>
        {
            var methods = builder.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods;
            if (methods is { Count: > 0 } && methods.All(m => m is not ("GET" or "HEAD")))
                builder.Metadata.Add(new AuthorizeAttribute(AuthorizationPolicies.AdminFull));
        });

        MapHealthEndpoints(group);
        MapMetricsEndpoints(group);
        MapQueueEndpoints(group);
        MapDeliveryLogEndpoints(group);
        MapUserEndpoints(group);
        MapDkimEndpoints(group);
        MapServerEndpoints(group);
        MapStatisticsEndpoints(group);
        MapTenantEndpoints(group);
        MapApiKeyEndpoints(group);

        // Configuration endpoints
        MapReceiveConnectorEndpoints(group);
        MapAcceptedDomainEndpoints(group);
        MapAcceptedSenderDomainEndpoints(group);
        MapIpAccessRuleEndpoints(group);
        MapSendConnectorEndpoints(group);
        MapDomainRouteEndpoints(group);
        MapDkimDomainEndpoints(group);
        MapRateLimitEndpoints(group);
        MapMessageFilterEndpoints(group);

        return endpoints;
    }

    private static void MapHealthEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/health", () => Results.Ok(new
        {
            Status = "Healthy",
            Timestamp = DateTimeOffset.UtcNow
        })).AllowAnonymous();
    }

    private static void MapMetricsEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/metrics", async (IMessageQueue q, RelayDbContext db, CancellationToken ct) =>
        {
            var process = Process.GetCurrentProcess();
            var queueDepth = await q.GetQueueDepthAsync(ct);
            var totalDeliveries = await db.DeliveryLogs.CountAsync(ct);
            var failedDeliveries = await db.DeliveryLogs.CountAsync(l => l.StatusCode.StartsWith("5"), ct);
            var totalMessages = await db.QueuedMessages.CountAsync(ct);

            return Results.Ok(new
            {
                Timestamp = DateTimeOffset.UtcNow,
                Queue = new { Depth = queueDepth, TotalProcessed = totalMessages },
                Deliveries = new { Total = totalDeliveries, Failed = failedDeliveries },
                Process = new
                {
                    UptimeSeconds = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
                    MemoryMB = process.WorkingSet64 / (1024.0 * 1024.0),
                    ThreadCount = process.Threads.Count
                }
            });
        });
    }

    private static void MapQueueEndpoints(RouteGroupBuilder group)
    {
        var queue = group.MapGroup("/queue");

        queue.MapGet("/status", async (IMessageQueue q, CancellationToken ct) =>
        {
            var depth = await q.GetQueueDepthAsync(ct);
            return Results.Ok(new QueueStatusResponse(depth));
        });

        queue.MapGet("/messages", async (IMessageQueue q, CancellationToken ct,
            int limit = 50) =>
        {
            var messages = await q.GetPendingAsync(limit, ct);
            return Results.Ok(messages.Select(m => new MessageSummary(
                m.Id, m.MessageId, m.Sender, m.Recipients, m.SizeBytes,
                m.Status, m.RetryCount, m.LastError, m.CreatedUtc, m.NextRetryUtc, m.CompletedUtc)));
        });

        queue.MapGet("/messages/{id:long}", async (long id, IMessageQueue q, CancellationToken ct) =>
        {
            var msg = await q.GetByIdAsync(id, ct);
            return msg is null ? Results.NotFound() : Results.Ok(msg);
        });

        queue.MapPost("/messages/{id:long}/retry", async (long id, IMessageQueue q, CancellationToken ct) =>
        {
            var msg = await q.GetByIdAsync(id, ct);
            if (msg is null) return Results.NotFound();
            if (msg.Status is not (MessageStatus.Failed or MessageStatus.Bounced))
                return Results.BadRequest(new { Error = "Only failed or bounced messages can be retried" });

            await q.UpdateStatusAsync(id, MessageStatus.Queued, null, ct);
            await q.SetRetryAsync(id, 0, DateTimeOffset.UtcNow, ct);
            return Results.Ok(new { Message = "Message re-queued for delivery" });
        });

        queue.MapDelete("/messages/{id:long}", async (long id, IMessageQueue q, CancellationToken ct) =>
        {
            var msg = await q.GetByIdAsync(id, ct);
            if (msg is null) return Results.NotFound();
            await q.DeleteAsync(id, ct);
            return Results.Ok(new { Message = "Message deleted" });
        });
    }

    private static void MapUserEndpoints(RouteGroupBuilder group)
    {
        var users = group.MapGroup("/users");

        users.MapGet("/", async (IUserService svc, CancellationToken ct) =>
        {
            var all = await svc.GetAllUsersAsync(ct);
            return Results.Ok(all.Select(u => new UserSummary(
                u.Id, u.Username, u.IsEnabled, u.AllowedSenderAddresses,
                u.RateLimitPerMinute, u.RateLimitPerDay, u.CreatedUtc)));
        });

        users.MapPost("/", async (CreateUserRequest req, IUserService svc, CancellationToken ct) =>
        {
            var existing = await svc.GetByUsernameAsync(req.Username, ct);
            if (existing is not null)
                return Results.Conflict(new { Error = $"User '{req.Username}' already exists" });

            await svc.CreateUserAsync(req.Username, req.Password, ct);
            return Results.Created($"/api/users/{req.Username}", new { Message = "User created" });
        });

        users.MapPut("/{id:int}", async (int id, UpdateUserRequest req, RelayDbContext db, CancellationToken ct) =>
        {
            var user = await db.RelayUsers.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();

            user.AllowedSenderAddresses = req.AllowedSenderAddresses;
            user.RateLimitPerMinute = req.RateLimitPerMinute;
            user.RateLimitPerDay = req.RateLimitPerDay;
            user.IsEnabled = req.IsEnabled;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { Message = "User updated" });
        });

        users.MapDelete("/{id:int}", async (int id, IUserService svc, CancellationToken ct) =>
        {
            await svc.DeleteUserAsync(id, ct);
            return Results.Ok(new { Message = "User deleted" });
        });
    }

    private static void MapDkimEndpoints(RouteGroupBuilder group)
    {
        // DKIM key generation/management deals with host-level private-key files — host admins only.
        group.MapPost("/dkim/generate", (DkimGenerateRequest req) =>
        {
            var (privateKey, publicKey, dnsTxt) = DkimKeyGenerator.GenerateKeyPair(
                req.Domain, req.Selector, req.KeySize > 0 ? req.KeySize : 2048);

            return Results.Ok(new
            {
                Domain = req.Domain,
                Selector = req.Selector,
                PrivateKeyPem = privateKey,
                PublicKeyPem = publicKey,
                DnsRecord = $"{req.Selector}._domainkey.{req.Domain}",
                DnsTxtValue = dnsTxt
            });
        }).RequireAuthorization(AuthorizationPolicies.HostAdmin);
    }

    private static void MapDeliveryLogEndpoints(RouteGroupBuilder group)
    {
        var logs = group.MapGroup("/deliverylogs");

        logs.MapGet("/", async (RelayDbContext db, CancellationToken ct,
            long? messageId = null, int limit = 50, int offset = 0) =>
        {
            var query = db.DeliveryLogs.AsNoTracking().AsQueryable();
            if (messageId.HasValue)
                query = query.Where(l => l.QueuedMessageId == messageId.Value);

            var items = await query
                .OrderByDescending(l => l.Id)
                .Skip(offset)
                .Take(limit)
                .Select(l => new DeliveryLogSummary(
                    l.Id, l.QueuedMessageId, l.Recipient, l.StatusCode,
                    l.StatusMessage, l.RemoteServer, l.TimestampUtc))
                .ToListAsync(ct);

            return Results.Ok(items);
        });

        logs.MapGet("/count", async (RelayDbContext db, CancellationToken ct) =>
        {
            var count = await db.DeliveryLogs.CountAsync(ct);
            return Results.Ok(new { Count = count });
        });
    }

    private static void MapServerEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/server/info", () =>
        {
            var assembly = typeof(AdminEndpoints).Assembly;
            var version = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString()
                          ?? "0.0.0";

            return Results.Ok(new
            {
                Version = version,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                StartedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
            });
        });
    }
    private static void MapTenantEndpoints(RouteGroupBuilder group)
    {
        // Tenant administration is host-only (in addition to the group's baseline authorization).
        var ep = group.MapGroup("/tenants").RequireAuthorization(AuthorizationPolicies.HostAdmin);

        ep.MapGet("/", async (ITenantService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAllAsync(ct)));

        ep.MapPost("/", async (CreateTenantRequest req, ITenantService svc, CancellationToken ct) =>
        {
            try
            {
                var created = await svc.CreateAsync(req.Name, req.Slug, ct);
                return Results.Created($"/api/tenants/{created.Id}", created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { Error = ex.Message });
            }
        });

        ep.MapPut("/{id:int}", async (int id, UpdateTenantRequest req, ITenantService svc, CancellationToken ct) =>
        {
            await svc.UpdateAsync(id, req.Name, req.IsEnabled, ct);
            return Results.Ok(new { Message = "Tenant updated" });
        });

        // Destructive: removes the tenant and ALL its data. Host-only (group is HostAdmin-gated).
        ep.MapDelete("/{id:int}", async (int id, ITenantService svc, CancellationToken ct) =>
        {
            try
            {
                await svc.PurgeAndDeleteAsync(id, ct);
                return Results.Ok(new { Message = "Tenant and all its data deleted" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });
    }

    private static void MapApiKeyEndpoints(RouteGroupBuilder group)
    {
        // API keys are sensitive — require AdminFull for the whole subgroup (incl. listing).
        var ep = group.MapGroup("/apikeys").RequireAuthorization(AuthorizationPolicies.AdminFull);

        ep.MapGet("/", async (IApiKeyService svc, ICurrentTenant tenant, CancellationToken ct) =>
        {
            int? tenantId = tenant.FilterEnabled ? tenant.FilterTenantId : null;
            var keys = await svc.GetAllAsync(tenantId, ct);
            return Results.Ok(keys.Select(k => new ApiKeySummary(
                k.Id, k.TenantId, k.Name, k.KeyPrefix, k.Role, k.IsEnabled, k.CreatedUtc, k.ExpiresUtc, k.LastUsedUtc)));
        });

        ep.MapPost("/", async (CreateApiKeyRequest req, IApiKeyService svc, ICurrentTenant tenant, CancellationToken ct) =>
        {
            if (!RelayRoles.All.Contains(req.Role))
                return Results.BadRequest(new { Error = "Invalid role" });

            int? tenantId;
            if (tenant.FilterEnabled)
            {
                // Tenant-scoped admins create only within their tenant and cannot mint host keys.
                if (req.Role == RelayRoles.HostAdmin)
                    return Results.Forbid();
                tenantId = tenant.FilterTenantId;
            }
            else
            {
                tenantId = req.TenantId; // host scope: null = host-level key, or a chosen tenant
            }

            var (key, plaintext) = await svc.CreateAsync(tenantId, req.Name, req.Role, req.ExpiresUtc, ct);
            return Results.Ok(new CreatedApiKeyResponse(key.Id, key.Name, key.KeyPrefix, key.Role, key.TenantId, plaintext));
        });

        ep.MapDelete("/{id:int}", async (int id, IApiKeyService svc, ICurrentTenant tenant, RelayDbContext db, CancellationToken ct) =>
        {
            var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct);
            if (key is null)
                return Results.NotFound();
            // Out-of-scope keys are invisible to a tenant-scoped admin.
            if (tenant.FilterEnabled && key.TenantId != tenant.FilterTenantId)
                return Results.NotFound();

            await svc.DeleteAsync(id, ct);
            return Results.Ok(new { Message = "API key revoked" });
        });
    }

    private static void MapStatisticsEndpoints(RouteGroupBuilder group)
    {
        var stats = group.MapGroup("/statistics");

        stats.MapGet("/live", async (IStatisticsService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetLiveStatisticsAsync(ct)));

        stats.MapGet("/hourly", async (IStatisticsService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetHourlyStatisticsAsync(ct)));

        stats.MapGet("/daily", async (IStatisticsService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetDailyStatisticsAsync(ct)));

        stats.MapGet("/monthly", async (IStatisticsService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetMonthlyStatisticsAsync(ct)));
    }

    private static void MapReceiveConnectorEndpoints(RouteGroupBuilder group)
    {
        // Receive connectors define the host's listening sockets — host-level infrastructure.
        var ep = group.MapGroup("/connectors/receive").RequireAuthorization(AuthorizationPolicies.HostAdmin);

        ep.MapGet("/", async (IReceiveConnectorService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAllAsync(ct)));

        ep.MapPost("/", async (ReceiveConnector connector, IReceiveConnectorService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(connector, ct);
            return Results.Created($"/api/connectors/receive/{created.Id}", created);
        });

        ep.MapPut("/{id:int}", async (int id, ReceiveConnector connector, IReceiveConnectorService svc, CancellationToken ct) =>
        {
            connector.Id = id;
            await svc.UpdateAsync(connector, ct);
            return Results.Ok(new { Message = "Receive connector updated" });
        });

        ep.MapDelete("/{id:int}", async (int id, IReceiveConnectorService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            return Results.Ok(new { Message = "Receive connector deleted" });
        });
    }

    private static void MapAcceptedDomainEndpoints(RouteGroupBuilder group)
    {
        var ep = group.MapGroup("/domains/accepted");

        ep.MapGet("/", async (IAcceptedDomainService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAllAsync(ct)));

        ep.MapPost("/", async (CreateAcceptedDomainRequest req, IAcceptedDomainService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            if (await svc.ExistsAsync(req.Domain, ct))
                return Results.Conflict(new { Error = $"Domain '{req.Domain}' already exists" });
            var created = await svc.CreateAsync(req.Domain, ct);
            cache.Invalidate();
            return Results.Created($"/api/domains/accepted/{created.Id}", created);
        });

        ep.MapDelete("/{id:int}", async (int id, IAcceptedDomainService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "Accepted domain deleted" });
        });
    }

    private static void MapAcceptedSenderDomainEndpoints(RouteGroupBuilder group)
    {
        var ep = group.MapGroup("/domains/accepted-sender");

        ep.MapGet("/", async (IAcceptedSenderDomainService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAllAsync(ct)));

        ep.MapPost("/", async (CreateAcceptedSenderDomainRequest req, IAcceptedSenderDomainService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            if (await svc.ExistsAsync(req.Domain, ct))
                return Results.Conflict(new { Error = $"Domain '{req.Domain}' already exists" });
            var created = await svc.CreateAsync(req.Domain, ct);
            cache.Invalidate();
            return Results.Created($"/api/domains/accepted-sender/{created.Id}", created);
        });

        ep.MapDelete("/{id:int}", async (int id, IAcceptedSenderDomainService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "Accepted sender domain deleted" });
        });
    }

    private static void MapIpAccessRuleEndpoints(RouteGroupBuilder group)
    {
        var ep = group.MapGroup("/ip-rules");

        ep.MapGet("/", async (IIpAccessRuleService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAllAsync(ct)));

        ep.MapPost("/", async (IpAccessRule rule, IIpAccessRuleService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(rule, ct);
            cache.Invalidate();
            return Results.Created($"/api/ip-rules/{created.Id}", created);
        });

        ep.MapPut("/{id:int}", async (int id, IpAccessRule rule, IIpAccessRuleService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            rule.Id = id;
            await svc.UpdateAsync(rule, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "IP access rule updated" });
        });

        ep.MapDelete("/{id:int}", async (int id, IIpAccessRuleService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "IP access rule deleted" });
        });
    }

    private static void MapSendConnectorEndpoints(RouteGroupBuilder group)
    {
        var ep = group.MapGroup("/connectors/send");

        ep.MapGet("/", async (ISendConnectorService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAllAsync(ct)));

        ep.MapPost("/", async (SendConnector connector, ISendConnectorService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(connector, ct);
            cache.Invalidate();
            return Results.Created($"/api/connectors/send/{created.Id}", created);
        });

        ep.MapPut("/{id:int}", async (int id, SendConnector connector, ISendConnectorService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            connector.Id = id;
            await svc.UpdateAsync(connector, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "Send connector updated" });
        });

        ep.MapDelete("/{id:int}", async (int id, ISendConnectorService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "Send connector deleted" });
        });
    }

    private static void MapDomainRouteEndpoints(RouteGroupBuilder group)
    {
        var ep = group.MapGroup("/routes");

        ep.MapGet("/", async (IDomainRouteService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAllAsync(ct)));

        ep.MapPost("/", async (DomainRoute route, IDomainRouteService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(route, ct);
            cache.Invalidate();
            return Results.Created($"/api/routes/{created.Id}", created);
        });

        ep.MapPut("/{id:int}", async (int id, DomainRoute route, IDomainRouteService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            route.Id = id;
            await svc.UpdateAsync(route, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "Domain route updated" });
        });

        ep.MapDelete("/{id:int}", async (int id, IDomainRouteService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "Domain route deleted" });
        });
    }

    private static void MapDkimDomainEndpoints(RouteGroupBuilder group)
    {
        // PrivateKeyPath is an arbitrary host-filesystem path, so per-tenant admins must not manage it
        // (it would let them probe server files or reference another tenant's key) — host admins only.
        var ep = group.MapGroup("/dkim/domains").RequireAuthorization(AuthorizationPolicies.HostAdmin);

        // The list never returns key material (PrivateKeyPem / PrivateKeyPath): the private key is the only
        // DKIM secret and must not leak over the wire. Project to a summary DTO that exposes only whether a
        // key is configured (HasPrivateKey).
        ep.MapGet("/", async (IDkimDomainService svc, CancellationToken ct) =>
        {
            var domains = await svc.GetAllAsync(ct);
            return Results.Ok(domains.Select(DkimDomainSummary.From));
        });

        ep.MapPost("/", async (DkimDomain dkim, IDkimDomainService svc, CancellationToken ct) =>
        {
            if (await svc.GetByDomainAsync(dkim.Domain, ct) is not null)
                return Results.Conflict(new { Error = $"DKIM config for '{dkim.Domain}' already exists" });
            var created = await svc.CreateAsync(dkim, ct);
            return Results.Created($"/api/dkim/domains/{created.Id}", created);
        });

        ep.MapPut("/{id:int}", async (int id, DkimDomain dkim, IDkimDomainService svc, CancellationToken ct) =>
        {
            dkim.Id = id;
            await svc.UpdateAsync(dkim, ct);
            return Results.Ok(new { Message = "DKIM domain updated" });
        });

        ep.MapDelete("/{id:int}", async (int id, IDkimDomainService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            return Results.Ok(new { Message = "DKIM domain deleted" });
        });
    }

    private static void MapRateLimitEndpoints(RouteGroupBuilder group)
    {
        var ep = group.MapGroup("/rate-limits");

        ep.MapGet("/", async (IRateLimitSettingsService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(ct)));

        ep.MapPut("/", async (RateLimitSettings settings, IRateLimitSettingsService svc, CancellationToken ct) =>
        {
            await svc.UpdateAsync(settings, ct);
            return Results.Ok(new { Message = "Rate limit settings updated" });
        });
    }

    private static void MapMessageFilterEndpoints(RouteGroupBuilder group)
    {
        var headers = group.MapGroup("/filters/headers");

        headers.MapGet("/", async (IMessageFilterService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetHeaderRulesAsync(ct)));

        headers.MapPost("/", async (HeaderRewriteEntry rule, IMessageFilterService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            var created = await svc.CreateHeaderRuleAsync(rule, ct);
            cache.Invalidate();
            return Results.Created($"/api/filters/headers/{created.Id}", created);
        });

        headers.MapPut("/{id:int}", async (int id, HeaderRewriteEntry rule, IMessageFilterService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            rule.Id = id;
            await svc.UpdateHeaderRuleAsync(rule, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "Header rewrite rule updated" });
        });

        headers.MapDelete("/{id:int}", async (int id, IMessageFilterService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            await svc.DeleteHeaderRuleAsync(id, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "Header rewrite rule deleted" });
        });

        var senders = group.MapGroup("/filters/senders");

        senders.MapGet("/", async (IMessageFilterService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetSenderRulesAsync(ct)));

        senders.MapPost("/", async (SenderRewriteEntry rule, IMessageFilterService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            var created = await svc.CreateSenderRuleAsync(rule, ct);
            cache.Invalidate();
            return Results.Created($"/api/filters/senders/{created.Id}", created);
        });

        senders.MapPut("/{id:int}", async (int id, SenderRewriteEntry rule, IMessageFilterService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            rule.Id = id;
            await svc.UpdateSenderRuleAsync(rule, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "Sender rewrite rule updated" });
        });

        senders.MapDelete("/{id:int}", async (int id, IMessageFilterService svc, IRuntimeConfigCache cache, CancellationToken ct) =>
        {
            await svc.DeleteSenderRuleAsync(id, ct);
            cache.Invalidate();
            return Results.Ok(new { Message = "Sender rewrite rule deleted" });
        });
    }
}

public record QueueStatusResponse(int Depth);

public record MessageSummary(
    long Id, string MessageId, string Sender, string Recipients, int SizeBytes,
    MessageStatus Status, int RetryCount, string? LastError,
    DateTimeOffset CreatedUtc, DateTimeOffset? NextRetryUtc, DateTimeOffset? CompletedUtc);

public record UserSummary(
    int Id, string Username, bool IsEnabled, string? AllowedSenderAddresses,
    int? RateLimitPerMinute, int? RateLimitPerDay, DateTimeOffset CreatedUtc);

public record CreateUserRequest(string Username, string Password);

public record UpdateUserRequest(
    bool IsEnabled, string? AllowedSenderAddresses,
    int? RateLimitPerMinute, int? RateLimitPerDay);

public record DkimGenerateRequest(string Domain, string Selector, int KeySize = 2048);

/// <summary>
/// Safe projection of a <see cref="DkimDomain"/> for API responses. Deliberately omits the private-key
/// material (PrivateKeyPem / PrivateKeyPath) — the only DKIM secret — exposing only whether a key is
/// configured via <see cref="HasPrivateKey"/>.
/// </summary>
public record DkimDomainSummary(
    int Id, int TenantId, string Domain, string Selector,
    bool IsEnabled, bool HasPrivateKey, DateTimeOffset CreatedUtc)
{
    public static DkimDomainSummary From(DkimDomain d) => new(
        d.Id, d.TenantId, d.Domain, d.Selector, d.IsEnabled,
        !string.IsNullOrEmpty(d.PrivateKeyPem) || !string.IsNullOrEmpty(d.PrivateKeyPath),
        d.CreatedUtc);
}

public record DeliveryLogSummary(
    long Id, long QueuedMessageId, string Recipient, string StatusCode,
    string StatusMessage, string? RemoteServer, DateTimeOffset TimestampUtc);

public record CreateAcceptedDomainRequest(string Domain);
public record CreateAcceptedSenderDomainRequest(string Domain);
public record CreateTenantRequest(string Name, string Slug);
public record UpdateTenantRequest(string Name, bool IsEnabled);

public record ApiKeySummary(
    int Id, int? TenantId, string Name, string KeyPrefix, string Role,
    bool IsEnabled, DateTimeOffset CreatedUtc, DateTimeOffset? ExpiresUtc, DateTimeOffset? LastUsedUtc);
public record CreateApiKeyRequest(string Name, string Role, int? TenantId, DateTimeOffset? ExpiresUtc);
public record CreatedApiKeyResponse(int Id, string Name, string KeyPrefix, string Role, int? TenantId, string Key);

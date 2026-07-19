using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.AdminApi.Mcp;

/// <summary>
/// MCP tools for troubleshooting and fixing the relay — a thin wrapper over the same services the
/// REST API uses. Tools run inside the authenticated HTTP request scope, so the tenant context, the
/// audit actor (ActorApiKeyId) and the runtime-config cache invalidation all work exactly as they do
/// for API calls. Every tool re-applies the role policy AND the API-key capability scope of the
/// equivalent endpoint (the /api scope filter does not cover /mcp), so an MCP session can never do
/// more than the same key could do against the REST API. The admin area (tenants, users, keys) is
/// deliberately not exposed as tools at all.
/// </summary>
[McpServerToolType]
public sealed class RelayMcpTools
{
    // ---- Diagnostics (reads) ----

    [McpServerTool(Name = "get_server_status"), Description(
        "Relay version, uptime and current queue depth — the first call to orient a troubleshooting session.")]
    public static async Task<object> GetServerStatus(
        ClaimsPrincipal user, IAuthorizationService auth, IMessageQueue queue, CancellationToken ct)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminView, ApiKeyScopes.Diag);
        var assembly = typeof(RelayMcpTools).Assembly;
        var process = Process.GetCurrentProcess();
        return new
        {
            Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            StartedUtc = process.StartTime.ToUniversalTime(),
            QueueDepth = await queue.GetQueueDepthAsync(ct),
        };
    }

    [McpServerTool(Name = "list_queue_messages"), Description(
        "Queued-message metadata (never bodies). By default only non-delivered messages — the ones worth investigating.")]
    public static async Task<IEnumerable<MessageSummary>> ListQueueMessages(
        ClaimsPrincipal user, IAuthorizationService auth, IMessageQueue queue, CancellationToken ct,
        [Description("Include already-delivered messages too")] bool includeDelivered = false,
        [Description("Maximum rows (1-200)")] int limit = 50)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminView, ApiKeyScopes.Messages);
        limit = Math.Clamp(limit, 1, 200);
        var messages = includeDelivered
            ? await queue.GetRecentAsync(limit, ct)
            : await queue.GetNonDeliveredAsync(limit, ct);
        return messages.Select(m => new MessageSummary(
            m.Id, m.MessageId, m.Sender, m.Recipients, m.SizeBytes,
            m.Status, m.RetryCount, m.LastError, m.CreatedUtc, m.NextRetryUtc, m.CompletedUtc));
    }

    [McpServerTool(Name = "get_message"), Description(
        "Metadata for one queued message: status, per-recipient delivery state, source IP, authenticated user. "
        + "The raw body is not available over MCP.")]
    public static async Task<MessageDetailResponse> GetMessage(
        ClaimsPrincipal user, IAuthorizationService auth, RelayDbContext db, CancellationToken ct,
        [Description("The queue message id")] long id)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminView, ApiKeyScopes.Messages);
        var detail = await db.QueuedMessages.AsNoTracking()
            .Where(m => m.Id == id)
            // EF/SQLite cannot translate byte[].Length — compare against the empty blob instead.
            .Select(m => new MessageDetailResponse(
                m.Id, m.MessageId, m.Sender, m.Recipients, m.SizeBytes,
                m.Status, m.RetryCount, m.LastError, m.CreatedUtc, m.NextRetryUtc, m.CompletedUtc,
                m.SourceIp, m.AuthenticatedUser, m.DeliveredRecipients,
                m.RawMessage != Array.Empty<byte>()))
            .FirstOrDefaultAsync(ct);
        return detail ?? throw new McpException($"Message {id} was not found.");
    }

    [McpServerTool(Name = "list_delivery_logs"), Description(
        "Per-recipient delivery attempts (SMTP status codes and remote-server responses), newest first.")]
    public static async Task<IEnumerable<DeliveryLogSummary>> ListDeliveryLogs(
        ClaimsPrincipal user, IAuthorizationService auth, RelayDbContext db, CancellationToken ct,
        [Description("Only attempts for this queue message id")] long? messageId = null,
        [Description("Maximum rows (1-200)")] int limit = 50)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminView, ApiKeyScopes.Diag);
        limit = Math.Clamp(limit, 1, 200);
        var query = db.DeliveryLogs.AsNoTracking().AsQueryable();
        if (messageId.HasValue)
            query = query.Where(l => l.QueuedMessageId == messageId.Value);
        return await query
            .OrderByDescending(l => l.Id)
            .Take(limit)
            .Select(l => new DeliveryLogSummary(
                l.Id, l.QueuedMessageId, l.Sender, l.Recipient, l.StatusCode,
                l.StatusMessage, l.RemoteServer, l.TimestampUtc))
            .ToListAsync(ct);
    }

    [McpServerTool(Name = "list_rejections"), Description(
        "Refused submissions, aggregated per client/reason/sender-domain — the place to look when a device "
        + "'sends but nothing arrives'. Trusted-source rows are misconfigured known devices; untrusted rows "
        + "are background noise the relay refused by design.")]
    public static async Task<IEnumerable<RejectionSummary>> ListRejections(
        ClaimsPrincipal user, IAuthorizationService auth, RelayDbContext db, ICurrentTenant tenant, CancellationToken ct,
        [Description("Also return rows an operator marked as expected/ignored")] bool includeIgnored = false)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminView, ApiKeyScopes.Diag);
        // RejectedSubmission is not tenant-owned (attribution may have failed) — split by hand.
        var query = db.RejectedSubmissions.AsNoTracking();
        if (tenant.FilterEnabled)
            query = query.Where(r => r.TenantId == tenant.FilterTenantId);
        if (!includeIgnored)
            query = query.Where(r => r.IgnoredUtc == null);
        var rows = await query.ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.LastSeenUtc)
            .Select(r => new RejectionSummary(
                r.Id, r.TenantId, r.ClientIp, r.Reason.ToString(), r.Reason.Describe(), r.ReplyCode,
                r.SenderDomain, r.Detail, r.IsTrustedSource, r.Reason.IsTemporary(), r.Count,
                r.FirstSeenUtc, r.LastSeenUtc, r.IgnoredUtc, r.LastBuffer));
    }

    [McpServerTool(Name = "get_health_check"), Description(
        "The latest daily self-check: DNS/deliverability/certificate/queue findings with remediation hints. Host admins only.")]
    public static async Task<object> GetHealthCheck(
        ClaimsPrincipal user, IAuthorizationService auth, IHealthCheckSnapshotService svc, CancellationToken ct)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.HostAdmin, ApiKeyScopes.Diag);
        var snapshot = await svc.GetLatestAsync(ct);
        return snapshot is null
            ? new { Message = "No self-check has run yet." }
            : HealthSnapshotDetail.From(snapshot);
    }

    [McpServerTool(Name = "query_audit"), Description(
        "Search the admin audit trail (who changed what, when — including API-key actors). Host admins only.")]
    public static async Task<AuditQueryResponse> QueryAudit(
        ClaimsPrincipal user, IAuthorizationService auth, IAdminAuditService svc, CancellationToken ct,
        [Description("Exact action key, e.g. 'sendconnector.updated'")] string? action = null,
        [Description("Free-text match on actor and detail")] string? search = null,
        [Description("Only events concerning this tenant")] int? tenantId = null,
        [Description("Maximum rows (1-500)")] int take = 100)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.HostAdmin, ApiKeyScopes.Diag);
        take = Math.Clamp(take, 1, 500);
        var (events, total) = await svc.QueryAsync(action, tenantId, search, 0, take, ct);
        return new AuditQueryResponse(total, events.Select(e => new AuditEventSummary(
            e.Id, e.OccurredUtc, e.Action, e.ActorUserId, e.ActorApiKeyId, e.ActorEmail,
            e.TargetUserId, e.TenantId, e.Detail)).ToList());
    }

    // ---- Configuration (reads) ----

    [McpServerTool(Name = "get_relay_config"), Description(
        "The relay's mail-flow configuration in one view: accepted domains, sender domains, IP rules, "
        + "send connectors (secrets omitted), routes and rate limits.")]
    public static async Task<object> GetRelayConfig(
        ClaimsPrincipal user, IAuthorizationService auth, CancellationToken ct,
        IAcceptedDomainService acceptedDomains, IAcceptedSenderDomainService senderDomains,
        IIpAccessRuleService ipRules, ISendConnectorService sendConnectors,
        IDomainRouteService routes, IRateLimitSettingsService rateLimits)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminView, ApiKeyScopes.Config);
        return new
        {
            AcceptedDomains = await acceptedDomains.GetAllAsync(ct),
            AcceptedSenderDomains = await senderDomains.GetAllAsync(ct),
            IpAccessRules = await ipRules.GetAllAsync(ct),
            SendConnectors = (await sendConnectors.GetAllAsync(ct)).Select(SendConnectorSummary.From),
            Routes = (await routes.GetAllAsync(ct)).Select(DomainRouteSummary.From),
            RateLimits = await rateLimits.GetAsync(ct),
        };
    }

    [McpServerTool(Name = "list_suppressions"), Description(
        "Addresses the relay will not deliver to (hard bounces, complaints, manual blocks) for the current scope.")]
    public static async Task<IEnumerable<SuppressionSummary>> ListSuppressions(
        ClaimsPrincipal user, IAuthorizationService auth, ISuppressionService svc, CancellationToken ct)
    {
        // Mirrors the Suppressions UI page and endpoint: AdminFull even for listing (recipient PII).
        await RequireAsync(auth, user, AuthorizationPolicies.AdminFull, ApiKeyScopes.Config);
        var entries = await svc.GetAllAsync(ct);
        return entries.Select(e => new SuppressionSummary(
            e.Id, e.TenantId, e.Address, e.Reason.ToString(), e.Detail, e.CreatedUtc));
    }

    // ---- Fixes (writes — all audited with the API-key actor) ----

    [McpServerTool(Name = "add_accepted_sender_domain"), Description(
        "Allow a sender domain (fixes 'sender domain not accepted' rejections). The change is audited and live immediately.")]
    public static async Task<object> AddAcceptedSenderDomain(
        ClaimsPrincipal user, IAuthorizationService auth, IAcceptedSenderDomainService svc, CancellationToken ct,
        [Description("The domain, e.g. 'acme.com'")] string domain)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminFull, ApiKeyScopes.Config, write: true);
        domain = domain.Trim().ToLowerInvariant();
        if (domain.Length == 0)
            throw new McpException("Domain must not be empty.");
        if (await svc.ExistsAsync(domain, ct))
            return new { Message = $"'{domain}' is already an accepted sender domain." };
        var created = await svc.CreateAsync(domain, ct);
        return new { Message = $"Sender domain '{domain}' accepted (id {created.Id}). Note: it is not ownership-verified yet." };
    }

    [McpServerTool(Name = "add_accepted_domain"), Description(
        "Add a hosted recipient domain (mail TO this domain is accepted for local/hosted delivery). Audited.")]
    public static async Task<object> AddAcceptedDomain(
        ClaimsPrincipal user, IAuthorizationService auth, IAcceptedDomainService svc, CancellationToken ct,
        [Description("The domain, e.g. 'acme.com'")] string domain)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminFull, ApiKeyScopes.Config, write: true);
        domain = domain.Trim().ToLowerInvariant();
        if (domain.Length == 0)
            throw new McpException("Domain must not be empty.");
        if (await svc.ExistsAsync(domain, ct))
            return new { Message = $"'{domain}' is already an accepted domain." };
        var created = await svc.CreateAsync(domain, ct);
        return new { Message = $"Recipient domain '{domain}' accepted (id {created.Id})." };
    }

    [McpServerTool(Name = "add_ip_allow_rule"), Description(
        "Allow submissions from a network (fixes 'not in allowed networks' rejections for a known device). "
        + "Prefer a narrow CIDR (/32 for one host). Audited.")]
    public static async Task<object> AddIpAllowRule(
        ClaimsPrincipal user, IAuthorizationService auth, IIpAccessRuleService svc, CancellationToken ct,
        [Description("Network in CIDR form, e.g. '192.168.1.10/32'")] string network,
        [Description("Why this network is allowed (shown in the rule list)")] string? description = null)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminFull, ApiKeyScopes.Config, write: true);
        network = network.Trim();
        if (network.Length == 0)
            throw new McpException("Network must not be empty.");
        var created = await svc.CreateAsync(new IpAccessRule
        {
            Network = network,
            Action = IpAccessAction.Allow,
            Description = description,
        }, ct);
        return new { Message = $"Allow rule for {network} created (id {created.Id})." };
    }

    [McpServerTool(Name = "add_suppression"), Description(
        "Manually block delivery to an address (e.g. a complainer). Audited.")]
    public static async Task<object> AddSuppression(
        ClaimsPrincipal user, IAuthorizationService auth, ISuppressionService svc, ICurrentTenant tenant, CancellationToken ct,
        [Description("The recipient address to suppress")] string address,
        [Description("Why (stored with the entry)")] string? note = null,
        [Description("Target tenant id — required when the key is host-scoped")] int? tenantId = null)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminFull, ApiKeyScopes.Config, write: true);
        int? tid = tenant.FilterEnabled ? tenant.FilterTenantId : tenantId;
        if (tid is not int resolvedTenant)
            throw new McpException("tenantId is required for a host-scoped key (a suppression belongs to one tenant).");
        await svc.AddAsync(address, SuppressionReason.Manual, note, resolvedTenant, ct);
        return new { Message = $"'{address.Trim().ToLowerInvariant()}' is now suppressed for tenant {resolvedTenant}." };
    }

    [McpServerTool(Name = "remove_suppression"), Description(
        "Remove a suppression so the address becomes deliverable again (use list_suppressions for the id). Audited.")]
    public static async Task<object> RemoveSuppression(
        ClaimsPrincipal user, IAuthorizationService auth, ISuppressionService svc, RelayDbContext db, CancellationToken ct,
        [Description("The suppression entry id")] int id)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminFull, ApiKeyScopes.Config, write: true);
        // The tenant query filter hides out-of-scope entries, so this doubles as authorization.
        if (!await db.SuppressionEntries.AsNoTracking().AnyAsync(e => e.Id == id, ct))
            throw new McpException($"Suppression {id} was not found.");
        await svc.RemoveAsync(id, ct);
        return new { Message = $"Suppression {id} removed — the address is deliverable again." };
    }

    [McpServerTool(Name = "requeue_message"), Description(
        "Re-queue a failed or bounced message for immediate delivery (after fixing the cause). Audited.")]
    public static async Task<object> RequeueMessage(
        ClaimsPrincipal user, IAuthorizationService auth, IMessageQueue queue, RelayDbContext db, CancellationToken ct,
        [Description("The queue message id")] long id)
    {
        await RequireAsync(auth, user, AuthorizationPolicies.AdminFull, ApiKeyScopes.Queue, write: true);
        if (!await db.QueuedMessages.AsNoTracking().AnyAsync(m => m.Id == id, ct))
            throw new McpException($"Message {id} was not found.");
        return await queue.RequeueAsync(id, ct)
            ? new { Message = $"Message {id} re-queued for delivery." }
            : new { Message = $"Message {id} is not in a retryable state (only Failed/Bounced can be re-queued)." };
    }

    /// <summary>
    /// Applies the same two gates the REST API applies: the role policy (via the registered
    /// authorization handlers, i.e. the consent-based membership model) and — for API-key callers —
    /// the capability scope. Access denial is an EXPECTED outcome for a scoped assistant, so it throws
    /// <see cref="McpException"/>: the SDK returns it as a tool error (IsError) with the message passed
    /// through to the caller, and — unlike a generic exception — does NOT log it at Error level. That
    /// keeps routine denials out of the log (and out of the daily self-check's new-error alert), and
    /// lets the assistant read exactly which scope it is missing.
    /// </summary>
    private static async Task RequireAsync(
        IAuthorizationService auth, ClaimsPrincipal user, string policy, string area, bool write = false)
    {
        var result = await auth.AuthorizeAsync(user, policy);
        if (!result.Succeeded)
            throw new McpException($"Access denied: this operation requires the '{policy}' role.");

        if (user.FindFirst(RelayClaimTypes.ApiKeyId) is null)
            return; // cookie admins are governed by role alone (mirrors the /api scope filter)

        var scopes = ApiKeyScopes.Parse(user.FindFirst(RelayClaimTypes.ApiKeyScopes)?.Value);
        var allowed = write ? ApiKeyScopes.AllowsWrite(scopes, area) : ApiKeyScopes.AllowsRead(scopes, area);
        if (!allowed)
        {
            var required = write ? ApiKeyScopes.Write(area) : ApiKeyScopes.Read(area);
            throw new McpException(
                $"This API key does not carry the required scope '{required}'. "
                + "A key without scopes is read-only; grant the scope on the API-key page.");
        }
    }
}

using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Computes the current tenant's setup readiness from its configuration. All counts come through
/// the tenant-scoped service interfaces, so they respect the ambient <see cref="ICurrentTenant"/>
/// query filter. DNS publication is not checked here (the page adds it with a live lookup).
/// </summary>
public class TenantReadinessService(
    ICurrentTenant currentTenant,
    ITenantService tenants,
    IUserService users,
    IAcceptedSenderDomainService senderDomains,
    IDkimDomainService dkimDomains,
    ISendConnectorService sendConnectors,
    IAcceptedDomainService recipientDomains,
    IIpAccessRuleService ipRules,
    IMessageFilterService messageFilters,
    IApiKeyService apiKeys) : ITenantReadinessService
{
    public async Task<TenantReadiness> GetAsync(CancellationToken ct = default)
    {
        // Readiness is per-tenant. In host/all-tenants scope there is no single tenant to assess,
        // so return an empty host-scope result; the page prompts the host to pick a tenant.
        if (currentTenant.TenantId is not { } tenantId)
            return new TenantReadiness(null, "", false, false, []);

        var tenant = await tenants.GetByIdAsync(tenantId, ct);
        var tenantName = tenant?.Name ?? "";
        var tenantActive = tenant?.IsEnabled ?? true;

        var userList = await users.GetAllUsersAsync(ct);
        var enabledUsers = userList.Count(u => u.IsEnabled);

        var senderList = await senderDomains.GetAllAsync(ct);
        var verifiedSenders = senderList.Count(d => d.VerifiedUtc is not null);

        var dkimList = await dkimDomains.GetAllAsync(ct);
        var enabledDkim = dkimList.Count(d => d.IsEnabled);

        var connectors = await sendConnectors.GetAllAsync(ct);
        var enabledConnectors = connectors.Count(c => c.IsEnabled);

        var recipientList = await recipientDomains.GetAllAsync(ct);
        var ipRuleList = await ipRules.GetAllAsync(ct);
        var headerRules = await messageFilters.GetHeaderRulesAsync(ct);
        var senderRules = await messageFilters.GetSenderRulesAsync(ct);
        var filterCount = headerRules.Count + senderRules.Count;
        var keyList = await apiKeys.GetAllAsync(tenantId, ct);

        // The hard minimum to relay mail: an active tenant with at least one credential to submit
        // with. (Unauthenticated IP-allowed submission is possible too, noted in the item detail.)
        var canSend = tenantActive && enabledUsers > 0;

        var items = new List<SetupItem>
        {
            // ----- Required: needed before any mail can flow -----
            new("tenant-active", "Organization active", SetupGroup.Required,
                tenantActive ? SetupStatus.Done : SetupStatus.Blocked,
                tenantActive ? "Approved and enabled by the host" : "Awaiting host approval",
                "", ""),

            new("smtp-users", "SMTP credentials", SetupGroup.Required,
                enabledUsers > 0 ? SetupStatus.Done : SetupStatus.Todo,
                enabledUsers > 0
                    ? $"{enabledUsers} user{Plural(enabledUsers)} can submit mail"
                    : "No SMTP users yet — clients need credentials to submit (or allow specific IPs without auth)",
                "/smtpusers", "Manage users"),

            // ----- Recommended: best practice for inbox deliverability -----
            new("sender-domains", "Sender domain(s)", SetupGroup.Recommended,
                senderList.Count > 0 ? SetupStatus.Done : SetupStatus.Permissive,
                senderList.Count > 0
                    ? $"{senderList.Count} domain{Plural(senderList.Count)} accepted"
                    : "Any From address is accepted — add your domains to control what you send as",
                "/domains/sender", "Manage sender domains"),

            new("sender-verified", "Verify domain ownership", SetupGroup.Recommended,
                senderList.Count == 0 ? SetupStatus.Blocked
                    : verifiedSenders == senderList.Count ? SetupStatus.Done
                    : verifiedSenders > 0 ? SetupStatus.Partial
                    : SetupStatus.Todo,
                senderList.Count == 0
                    ? "Add a sender domain first"
                    : $"{verifiedSenders} of {senderList.Count} verified",
                "/domains/sender", "Verify ownership"),

            new("dkim", "DKIM signing", SetupGroup.Recommended,
                enabledDkim > 0 ? SetupStatus.Done : SetupStatus.Todo,
                enabledDkim > 0
                    ? $"{enabledDkim} domain{Plural(enabledDkim)} signing outbound mail"
                    : "Not set up — DKIM lets recipients verify your mail is genuine",
                "/dkim", "Set up DKIM"),

            // ----- Optional: sensible defaults already apply -----
            new("outbound", "Outbound delivery", SetupGroup.Optional,
                SetupStatus.Done,
                enabledConnectors > 0
                    ? $"{enabledConnectors} send connector{Plural(enabledConnectors)} configured"
                    : "Direct-to-MX (default) — add a send connector to route via a smart host",
                "/connectors/send", "Send connectors"),

            new("recipient-domains", "Recipient domains (inbound)", SetupGroup.Optional,
                recipientList.Count > 0 ? SetupStatus.Done : SetupStatus.Permissive,
                recipientList.Count > 0
                    ? $"Accepts inbound mail for {recipientList.Count} domain{Plural(recipientList.Count)}"
                    : "Outbound only — not acting as inbound MX for any domain",
                "/domains", "Recipient domains"),

            new("ip-rules", "IP access rules", SetupGroup.Optional,
                ipRuleList.Count > 0 ? SetupStatus.Done : SetupStatus.Permissive,
                ipRuleList.Count > 0
                    ? $"{ipRuleList.Count} rule{Plural(ipRuleList.Count)} configured"
                    : "No IP restrictions — submission is controlled by credentials",
                "/ip-rules", "IP access rules"),

            new("filters", "Message filters", SetupGroup.Optional,
                filterCount > 0 ? SetupStatus.Done : SetupStatus.Permissive,
                filterCount > 0
                    ? $"{filterCount} rewrite rule{Plural(filterCount)} active"
                    : "No header/sender rewriting rules",
                "/filters", "Message filters"),

            new("api-keys", "API keys", SetupGroup.Optional,
                keyList.Count > 0 ? SetupStatus.Done : SetupStatus.Permissive,
                keyList.Count > 0
                    ? $"{keyList.Count} API key{Plural(keyList.Count)} issued"
                    : "No API keys issued",
                "/apikeys", "API keys"),

            new("egress-ip", "Dedicated sending IP", SetupGroup.Optional,
                string.IsNullOrWhiteSpace(tenant?.EgressIpAddress) ? SetupStatus.Permissive : SetupStatus.Done,
                string.IsNullOrWhiteSpace(tenant?.EgressIpAddress)
                    ? "Shared sending IP (default)"
                    : $"Sending from {tenant!.EgressIpAddress}",
                "", "")
        };

        return new TenantReadiness(tenantId, tenantName, tenantActive, canSend, items);
    }

    private static string Plural(int n) => n == 1 ? "" : "s";
}

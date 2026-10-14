using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Health;

/// <summary>
/// How outbound mail leaves this relay, as far as the self-check needs to know: whether any message can be
/// delivered directly to a recipient's MX (then this server's IP, EHLO name, PTR, SPF and port 25 are what
/// receivers judge), and which smart hosts carry the rest (then the smart host's IP and DKIM signing are what
/// receivers judge, and the direct-delivery checks do not apply).
/// </summary>
public sealed record OutboundRouting(bool DirectMxPossible, IReadOnlyList<string> SmartHosts)
{
    /// <summary>Every message leaves through a smart host — nothing is delivered to a recipient's MX directly.</summary>
    public bool SmartHostOnly => !DirectMxPossible && SmartHosts.Count > 0;

    /// <summary>Human-readable list of the smart hosts, e.g. "smtp-relay.brevo.com".</summary>
    public string SmartHostList => string.Join(", ", SmartHosts);

    /// <summary>
    /// Mirrors the delivery order in SmtpDeliveryService: a matching domain route wins, then the tenant's
    /// enabled default connector when it has a smart host, then the global smart host (appsettings), else
    /// direct MX. Direct MX is therefore possible when an enabled tenant has neither a smart-host default
    /// connector nor a global smart host to fall back on, or when a domain route points at a connector
    /// without a smart host.
    /// </summary>
    public static OutboundRouting Evaluate(
        IEnumerable<Tenant> tenants,
        IEnumerable<SendConnector> connectors,
        IEnumerable<DomainRoute> routes,
        string? globalSmartHost)
    {
        var connectorList = connectors.ToList();
        var byId = connectorList.ToDictionary(c => c.Id);
        var hasGlobal = !string.IsNullOrWhiteSpace(globalSmartHost);
        var smartHosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var direct = false;

        foreach (var tenant in tenants.Where(t => t.IsEnabled))
        {
            var defaultConnector = connectorList
                .Where(c => c.TenantId == tenant.Id && c.IsDefault && c.IsEnabled)
                .OrderBy(c => c.Id)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(defaultConnector?.SmartHost))
                smartHosts.Add(defaultConnector.SmartHost.Trim());
            else if (hasGlobal)
                smartHosts.Add(globalSmartHost!.Trim());
            else
                direct = true;
        }

        foreach (var route in routes)
        {
            var connector = route.SendConnector ?? byId.GetValueOrDefault(route.SendConnectorId);
            if (connector is null)
                continue;
            if (string.IsNullOrWhiteSpace(connector.SmartHost))
                direct = true;
            else
                smartHosts.Add(connector.SmartHost.Trim());
        }

        return new OutboundRouting(direct, [.. smartHosts]);
    }
}

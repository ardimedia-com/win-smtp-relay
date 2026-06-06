using System.Net;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.SmtpListener;

public static class IpAccessEvaluator
{
    /// <summary>
    /// Evaluates ordered IP access rules with first-match-wins semantics (by SortOrder):
    /// <list type="bullet">
    /// <item>first matching <see cref="IpAccessAction.Deny"/> rule → <c>false</c> (blocked)</item>
    /// <item>first matching <see cref="IpAccessAction.Allow"/> rule → <c>true</c> (permitted)</item>
    /// <item>no rule matches → permitted only in deny-list mode (no Allow rules present);
    /// if any Allow rule exists, an unmatched client is blocked (allow-list mode)</item>
    /// </list>
    /// Returns <c>null</c> when there are no rules at all, so the caller can apply a fallback.
    /// </summary>
    /// <summary>
    /// Evaluates IP access rules for a single tenant on the shared listener. A tenant's own
    /// rules are evaluated first (by their SortOrder), then the host/default-tenant baseline —
    /// so one tenant's rules can never allow or deny another tenant's traffic, while the host
    /// baseline (e.g. the seeded private-network allow-list) still applies to everyone.
    /// </summary>
    public static bool? EvaluateForTenant(IPAddress clientIp, IReadOnlyList<IpAccessRule> allRules, int tenantId)
    {
        var hostRules = allRules.Where(r => r.TenantId == TenantDefaults.DefaultTenantId).OrderBy(r => r.SortOrder);

        // The default tenant IS the host baseline — evaluate it once, not twice.
        var effective = tenantId == TenantDefaults.DefaultTenantId
            ? hostRules.ToList()
            : allRules.Where(r => r.TenantId == tenantId).OrderBy(r => r.SortOrder).Concat(hostRules).ToList();

        return Evaluate(clientIp, effective);
    }

    public static bool? Evaluate(IPAddress clientIp, IReadOnlyList<IpAccessRule> rulesOrderedBySortOrder)
    {
        if (rulesOrderedBySortOrder.Count == 0)
            return null;

        var hasAllowRule = false;
        foreach (var rule in rulesOrderedBySortOrder)
        {
            if (rule.Action == IpAccessAction.Allow)
                hasAllowRule = true;

            if (IpNetworkHelper.IsInNetwork(clientIp, rule.Network))
                return rule.Action == IpAccessAction.Allow;
        }

        // No rule matched: allow-list mode blocks, deny-list mode permits.
        return !hasAllowRule;
    }

    /// <summary>
    /// Relay authorization for an UNAUTHENTICATED client (open-relay protection). Returns <c>true</c>
    /// only if the client is matched by an EXPLICIT, non-"any" <see cref="IpAccessAction.Allow"/> rule
    /// (first-match, the tenant's own rules then the host baseline), or is in a non-"any" entry of the
    /// static appsettings allow-list. A first-matching <see cref="IpAccessAction.Deny"/> — or no match
    /// at all — returns <c>false</c>. Crucially, an "any" rule (0.0.0.0/0 or ::/0), and an empty
    /// configuration, do NOT authorize relaying: this makes an open relay impossible to create by
    /// configuration alone. (Connection-level acceptance still uses <see cref="EvaluateForTenant"/>;
    /// this is the stricter gate applied only when relaying to a non-hosted, external recipient.)
    /// </summary>
    public static bool IsExplicitlyAllowedForRelay(
        IPAddress clientIp,
        IReadOnlyList<IpAccessRule> allRules,
        int tenantId,
        IReadOnlyList<string> staticAllowedNetworks)
    {
        var hostRules = allRules.Where(r => r.TenantId == TenantDefaults.DefaultTenantId).OrderBy(r => r.SortOrder);
        var effective = tenantId == TenantDefaults.DefaultTenantId
            ? hostRules.ToList()
            : allRules.Where(r => r.TenantId == tenantId).OrderBy(r => r.SortOrder).Concat(hostRules).ToList();

        foreach (var rule in effective)
        {
            if (!IpNetworkHelper.IsInNetwork(clientIp, rule.Network))
                continue;
            // First matching rule decides. An overly-broad Allow rule does NOT grant relay rights.
            return rule.Action == IpAccessAction.Allow && !IsTooBroadForRelay(rule.Network);
        }

        // No DB rule matched — honour specific-enough entries in the static appsettings allow-list.
        return staticAllowedNetworks.Any(n => !IsTooBroadForRelay(n) && IpNetworkHelper.IsInNetwork(clientIp, n));
    }

    /// <summary>True for a CIDR covering the entire address space (prefix length 0), e.g. 0.0.0.0/0 or ::/0.</summary>
    public static bool IsAnyNetwork(string cidr)
    {
        var parts = cidr.Split('/');
        return parts.Length > 1 && int.TryParse(parts[1], out var prefix) && prefix == 0;
    }

    // Minimum prefix lengths an allow rule must have to authorize RELAYING. Anything broader (a smaller
    // prefix) is treated as effectively "any" and does NOT grant relay — this closes the loophole where
    // a pair like 0.0.0.0/1 + 128.0.0.0/1 (each non-zero, so not "any") would together cover the whole
    // address space and re-create an open relay. Inbound acceptance is unaffected; only external relay
    // authorization is gated this strictly.
    private const int MinRelayPrefixV4 = 8;
    private const int MinRelayPrefixV6 = 16;

    /// <summary>
    /// True if a CIDR is too broad to authorize relaying (prefix shorter than the per-family minimum),
    /// or is malformed (fail-safe: a rule we can't parse never grants relay).
    /// </summary>
    public static bool IsTooBroadForRelay(string cidr)
    {
        var parts = cidr.Split('/');
        if (!IPAddress.TryParse(parts[0].Trim(), out var addr))
            return true;

        var isV6 = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !addr.IsIPv4MappedToIPv6;
        var max = isV6 ? 128 : 32;

        int prefix;
        if (parts.Length > 1)
        {
            if (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > max)
                return true; // malformed prefix → fail safe
        }
        else
        {
            prefix = max; // a bare address is a single host (/32 or /128)
        }

        return prefix < (isV6 ? MinRelayPrefixV6 : MinRelayPrefixV4);
    }
}

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
            // First matching rule decides. An "any" Allow rule does NOT grant relay rights.
            return rule.Action == IpAccessAction.Allow && !IsAnyNetwork(rule.Network);
        }

        // No DB rule matched — honour explicit, non-"any" entries in the static appsettings allow-list.
        return staticAllowedNetworks.Any(n => !IsAnyNetwork(n) && IpNetworkHelper.IsInNetwork(clientIp, n));
    }

    /// <summary>True for a CIDR covering the entire address space (prefix length 0), e.g. 0.0.0.0/0 or ::/0.</summary>
    public static bool IsAnyNetwork(string cidr)
    {
        var parts = cidr.Split('/');
        return parts.Length > 1 && int.TryParse(parts[1], out var prefix) && prefix == 0;
    }
}

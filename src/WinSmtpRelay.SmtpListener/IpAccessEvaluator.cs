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
}

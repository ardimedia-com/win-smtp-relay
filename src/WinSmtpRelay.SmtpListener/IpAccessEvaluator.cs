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

using System.Net;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// Decides which tenant an <em>unauthenticated</em> SMTP submission belongs to. Authenticated sessions
/// never reach here — their tenant comes from the SMTP user. The two strict-mode flags
/// (<c>BindTenantToAllowIpRule</c>, <c>RejectUnresolvedTenant</c>) on <see cref="EmailAuthSettings"/>
/// harden attribution against cross-tenant sender-domain spoofing and silent default-tenant fallback.
/// Pure (no I/O) so it is straightforward to unit-test.
/// </summary>
public static class UnauthenticatedTenantResolver
{
    public enum Outcome
    {
        /// <summary>A tenant was determined; <c>TenantId</c> is set.</summary>
        Resolved,
        /// <summary>The client IP matches Allow rules of more than one tenant (cannot bind).</summary>
        AmbiguousIp,
        /// <summary>The MAIL FROM domain belongs to a different tenant than the IP-bound one (spoofing).</summary>
        CrossTenantDomain,
        /// <summary>No tenant could be attributed and the default fallback is disabled.</summary>
        Unresolved
    }

    /// <summary>
    /// The single non-default tenant whose Allow rule matches <paramref name="clientIp"/>, or
    /// <c>Ambiguous = true</c> when more than one distinct tenant matches. Host-baseline (default-tenant)
    /// Allow rules are shared and never bind a specific tenant, so they are ignored here.
    /// </summary>
    public static (int? TenantId, bool Ambiguous) TenantFromAllowRules(IPAddress clientIp, IReadOnlyList<IpAccessRule> rules)
    {
        var matched = rules
            .Where(r => r.Action == IpAccessAction.Allow
                        && r.TenantId != TenantDefaults.DefaultTenantId
                        && IpNetworkHelper.IsInNetwork(clientIp, r.Network))
            .Select(r => r.TenantId)
            .Distinct()
            .ToList();

        return matched.Count switch
        {
            0 => (null, false),
            1 => (matched[0], false),
            _ => (null, true),
        };
    }

    /// <summary>
    /// Combines the candidate from IP (<paramref name="ipTenant"/>/<paramref name="ipAmbiguous"/>) and the
    /// candidate from the sender domain (<paramref name="domainTenant"/>) under the two strict-mode flags.
    /// </summary>
    public static (int? TenantId, Outcome Outcome) Decide(
        int? domainTenant,
        int? ipTenant,
        bool ipAmbiguous,
        bool bindTenantToAllowIpRule,
        bool rejectUnresolvedTenant)
    {
        if (bindTenantToAllowIpRule)
        {
            if (ipAmbiguous)
                return (null, Outcome.AmbiguousIp);

            if (ipTenant is { } boundTenant)
            {
                // The sender domain must not be claimed by a different tenant (anti-spoofing). An
                // unclaimed domain is allowed and attributed to the IP-bound tenant.
                if (domainTenant is { } dt && dt != boundTenant)
                    return (null, Outcome.CrossTenantDomain);
                return (boundTenant, Outcome.Resolved);
            }
        }

        // Not bound by IP — attribute by sender domain.
        if (domainTenant is { } domainOwner)
            return (domainOwner, Outcome.Resolved);

        // Nothing matched.
        return rejectUnresolvedTenant
            ? (null, Outcome.Unresolved)
            : (TenantDefaults.DefaultTenantId, Outcome.Resolved);
    }
}

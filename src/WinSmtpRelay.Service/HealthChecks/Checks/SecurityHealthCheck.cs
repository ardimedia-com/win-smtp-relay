using System.Net;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// Relay-security posture: confirms open-relay protection is in force (always-on, enforced in code) and
/// flags overly-broad allow-IP rules that would let untrusted/public networks relay without authentication.
/// </summary>
public sealed class SecurityHealthCheck(
    IRuntimeConfigCache cache,
    ILogger<SecurityHealthCheck> logger) : HealthCheckBase
{
    public override string Name => "Security";
    protected override string Category => HealthCategories.Security;

    public override async Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct)
    {
        var f = new List<HealthFinding>
        {
            Ok("open-relay-protection", "Open-relay protection is active",
                "Relaying to a domain this server does not host always requires SMTP authentication or an explicit allow-IP rule — enforced in code and not configurable."),
        };

        IReadOnlyList<IpAccessRule> rules;
        try { rules = await cache.GetIpAccessRulesAsync(ct); }
        catch (Exception ex) { logger.LogWarning(ex, "loading IP access rules failed"); return f; }

        foreach (var net in rules.Where(r => r.Action == IpAccessAction.Allow).Select(r => r.Network.Trim()).Where(n => n.Length > 0))
        {
            if (net.EndsWith("/0"))
                f.Add(Warn("broad-allow", $"Allow rule {net} permits any address",
                    "An 'any' allow rule lets every IP connect. It does not by itself grant unauthenticated external relay, but it is very broad — confirm it is intended.",
                    net, "Narrow it to the specific networks that need access."));
            else if (IsPublicNetwork(net))
                f.Add(Warn("broad-allow", $"Allow rule {net} covers public address space",
                    "This allow-IP rule grants unauthenticated relay from public IP space. Make sure only trusted networks are allowed — otherwise external senders could relay through this server.",
                    net, "Restrict allow rules to your own/trusted networks and require SMTP AUTH for everyone else."));
        }

        return f;
    }

    /// <summary>True if the network's base address is a public (non-private, non-loopback) address.</summary>
    private static bool IsPublicNetwork(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var baseIp = slash >= 0 ? cidr[..slash] : cidr;
        return IPAddress.TryParse(baseIp.Trim(), out var ip) && !IpAddressScope.IsPrivateOrLoopback(ip);
    }
}

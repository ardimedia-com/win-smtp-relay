using System.Net;
using Microsoft.Extensions.Logging;
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
        return IPAddress.TryParse(baseIp.Trim(), out var ip) && !IsPrivateOrLoopback(ip);
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        var b = ip.GetAddressBytes();
        if (b.Length == 4)
            return b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                 // 169.254.0.0/16 link-local
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);  // 100.64.0.0/10 CGNAT

        if (b.Length == 16)
            return (b[0] & 0xFE) == 0xFC          // fc00::/7 unique-local
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80); // fe80::/10 link-local

        return false;
    }
}

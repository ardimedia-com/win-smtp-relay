using System.Net;
using System.Net.NetworkInformation;

namespace WinSmtpRelay.Core;

/// <summary>
/// Classifies addresses as private (never seen by receivers) or public, and answers whether an address is
/// assigned to this machine. Shared by the self-check, the DNS setup page and the input validation of the
/// sending identity and the tenant source IP, so all of them draw the same line.
/// </summary>
public static class IpAddressScope
{
    /// <summary>
    /// True for loopback, RFC 1918, link-local, CGNAT (100.64/10), IPv6 unique-local and link-local addresses —
    /// addresses that only exist behind a NAT/router and that a receiving mail server never sees.
    /// </summary>
    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
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
            return (b[0] & 0xFE) == 0xFC                        // fc00::/7 unique-local
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);     // fe80::/10 link-local

        return false;
    }

    /// <summary>
    /// Parses an address or a network in CIDR form (<c>1.2.3.4</c>, <c>1.2.3.0/24</c>, <c>2001:db8::/48</c>) and
    /// returns its base address. False for anything else.
    /// </summary>
    public static bool TryParseAddressOrNetwork(string value, out IPAddress address)
    {
        address = IPAddress.None;
        var text = value.Trim();
        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            if (!int.TryParse(text[(slash + 1)..], out var prefix) || prefix < 0)
                return false;
            text = text[..slash];
            if (!IPAddress.TryParse(text, out var network))
                return false;
            var max = network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            if (prefix > max)
                return false;
            address = network;
            return true;
        }

        if (!IPAddress.TryParse(text, out var parsed))
            return false;
        address = parsed;
        return true;
    }

    /// <summary>True when <paramref name="ip"/> is assigned to one of this machine's network adapters.</summary>
    public static bool IsAssignedLocally(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        return LocalAddresses().Any(a => a.Equals(ip));
    }

    /// <summary>Every unicast address of every network adapter on this machine.</summary>
    public static IEnumerable<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address);
}

using System.Net;
using System.Net.Sockets;

namespace WinSmtpRelay.SmtpListener;

public static class IpNetworkHelper
{
    public static bool IsInAnyNetwork(IPAddress clientIp, IEnumerable<string> cidrNetworks)
    {
        foreach (var cidr in cidrNetworks)
        {
            if (IsInNetwork(clientIp, cidr))
                return true;
        }
        return false;
    }

    public static bool IsInNetwork(IPAddress clientIp, string cidr)
    {
        var parts = cidr.Split('/');
        if (!IPAddress.TryParse(parts[0], out var networkAddress))
            return false;

        int prefixLength;
        if (parts.Length > 1)
        {
            // CIDR rules come from admin input and this runs on the SMTP hot path — a malformed or
            // negative prefix must never throw; treat it as a non-match.
            if (!int.TryParse(parts[1], out prefixLength) || prefixLength < 0)
                return false;
        }
        else
        {
            prefixLength = networkAddress.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        }

        // Normalize IPv4-mapped IPv6 addresses
        if (clientIp.IsIPv4MappedToIPv6)
            clientIp = clientIp.MapToIPv4();
        if (networkAddress.IsIPv4MappedToIPv6)
            networkAddress = networkAddress.MapToIPv4();

        if (clientIp.AddressFamily != networkAddress.AddressFamily)
            return false;

        var clientBytes = clientIp.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();

        // A prefix longer than the (possibly v4-normalized) address can never match — guards the
        // byte loop below against an out-of-range index.
        if (prefixLength > clientBytes.Length * 8)
            return false;

        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (clientBytes[i] != networkBytes[i])
                return false;
        }

        if (remainingBits > 0 && fullBytes < clientBytes.Length)
        {
            byte mask = (byte)(0xFF << (8 - remainingBits));
            if ((clientBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }
}

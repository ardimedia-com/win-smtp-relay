using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinSmtpRelay.AdminUi.Services;

/// <summary>
/// Host-network helpers for the outbound-IP UI: enumerates the machine's local IPv4 addresses (so an
/// operator can pick the sending IP from a list instead of typing it) and tests whether outbound SMTP
/// actually works from a chosen source IP. Runs on the relay host (the Blazor circuit), which is the same
/// machine that sends mail, so the results reflect the real egress path.
/// </summary>
public sealed class LocalNetworkProbe
{
    /// <summary>The machine's usable local IPv4 addresses (excludes loopback and link-local).</summary>
    public IReadOnlyList<string> GetLocalIpv4Addresses()
    {
        var ips = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var s = ua.Address.ToString();
                if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue; // APIPA / link-local
                if (!ips.Contains(s)) ips.Add(s);
            }
        }
        return ips;
    }

    /// <summary>
    /// Tries to open a TCP connection to <paramref name="host"/>:<paramref name="port"/> bound to
    /// <paramref name="sourceIp"/> (null/blank = OS default), with a short timeout. Tells the operator
    /// whether outbound SMTP actually works from that source IP — distinguishing a blocked port / route
    /// from a working path. The target host:port are caller-fixed (no user-supplied address) so this
    /// cannot be used to probe arbitrary hosts.
    /// </summary>
    public async Task<OutboundProbeResult> TestOutboundAsync(
        string? sourceIp, string host, int port, int timeoutSeconds = 10, CancellationToken ct = default)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (!string.IsNullOrWhiteSpace(sourceIp))
            {
                if (!IPAddress.TryParse(sourceIp.Trim(), out var src))
                    return new OutboundProbeResult(false, $"'{sourceIp}' is not a valid IP address.");
                socket.Bind(new IPEndPoint(src, 0));
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await socket.ConnectAsync(host, port, cts.Token);
            return new OutboundProbeResult(true, "connected — outbound works from this source.");
        }
        catch (OperationCanceledException)
        {
            return new OutboundProbeResult(false, "timed out — the port is likely blocked, or this source IP can't route out.");
        }
        catch (SocketException ex)
        {
            return new OutboundProbeResult(false, $"failed ({ex.SocketErrorCode}).");
        }
        catch (Exception ex)
        {
            return new OutboundProbeResult(false, ex.Message);
        }
    }
}

/// <summary>Result of <see cref="LocalNetworkProbe.TestOutboundAsync"/>.</summary>
public sealed record OutboundProbeResult(bool Ok, string Message);

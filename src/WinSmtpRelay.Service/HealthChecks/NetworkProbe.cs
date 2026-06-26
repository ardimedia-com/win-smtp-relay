using System.Net.Sockets;

namespace WinSmtpRelay.Service.HealthChecks;

/// <summary>Small helper for the network-touching checks: a bounded TCP connect test.</summary>
internal static class NetworkProbe
{
    /// <summary>True if a TCP connection to <paramref name="host"/>:<paramref name="port"/> opens within the timeout.</summary>
    public static async Task<bool> CanConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

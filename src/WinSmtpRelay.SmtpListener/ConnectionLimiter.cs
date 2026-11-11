using System.Net;

namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// Counts concurrent SMTP sessions, globally and per client IP, so one client (or a flood of them) cannot
/// hold every session slot: each open session costs a socket, buffers and a task. A slot is taken when the
/// listener accepts a connection and released when the session's pipe is disposed. A limit of 0 disables it.
/// </summary>
public sealed class ConnectionLimiter(int maxConnections, int maxConnectionsPerIp)
{
    private readonly Dictionary<IPAddress, int> _perIp = [];
    private readonly object _gate = new();
    private int _total;

    /// <summary>Sessions currently open.</summary>
    public int ActiveConnections
    {
        get { lock (_gate) return _total; }
    }

    /// <summary>Sessions currently open from <paramref name="address"/>.</summary>
    public int ActiveConnectionsFrom(IPAddress address)
    {
        lock (_gate)
            return _perIp.TryGetValue(Normalize(address), out var n) ? n : 0;
    }

    /// <summary>Takes a slot for <paramref name="address"/>; false when either cap is reached (nothing is taken then).</summary>
    public bool TryAcquire(IPAddress? address)
    {
        lock (_gate)
        {
            if (maxConnections > 0 && _total >= maxConnections)
                return false;

            if (address is not null)
            {
                var key = Normalize(address);
                var current = _perIp.TryGetValue(key, out var n) ? n : 0;
                if (maxConnectionsPerIp > 0 && current >= maxConnectionsPerIp)
                    return false;
                _perIp[key] = current + 1;
            }

            _total++;
            return true;
        }
    }

    /// <summary>Returns the slot taken by <see cref="TryAcquire"/>.</summary>
    public void Release(IPAddress? address)
    {
        lock (_gate)
        {
            if (_total > 0)
                _total--;

            if (address is null)
                return;

            var key = Normalize(address);
            if (!_perIp.TryGetValue(key, out var n))
                return;

            if (n <= 1)
                _perIp.Remove(key);
            else
                _perIp[key] = n - 1;
        }
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

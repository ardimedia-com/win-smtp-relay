using System.Collections.Concurrent;
using System.Net;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// Counts failed API-key authentications per client IP in a fixed window and blocks the address for the rest of
/// the window once it exceeds the limit — so a client cannot grind through keys (or load the database with
/// prefix lookups) at network speed. A blocked request is refused before the key is looked up and answered with
/// 429. Valid keys are never throttled. Singleton; entries expire with their window.
/// </summary>
public sealed class ApiKeyFailureThrottle(TimeProvider time)
{
    /// <summary>Failed attempts allowed per IP and window.</summary>
    public const int MaxFailures = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary><c>HttpContext.Items</c> key set when a request was refused by the throttle (→ 429, not 401).</summary>
    public const string BlockedItemKey = "WinSmtpRelay.ApiKeyThrottled";

    private const int SweepThreshold = 10_000;

    private readonly ConcurrentDictionary<IPAddress, Entry> _entries = new();

    public ApiKeyFailureThrottle() : this(TimeProvider.System) { }

    public bool IsBlocked(IPAddress? ip)
    {
        if (ip is null || !_entries.TryGetValue(Normalize(ip), out var entry))
            return false;
        var now = time.GetUtcNow();
        return now - entry.WindowStart < Window && entry.Failures >= MaxFailures;
    }

    public void RecordFailure(IPAddress? ip)
    {
        if (ip is null)
            return;
        var now = time.GetUtcNow();
        _entries.AddOrUpdate(Normalize(ip),
            _ => new Entry(now, 1),
            (_, e) => now - e.WindowStart >= Window ? new Entry(now, 1) : e with { Failures = e.Failures + 1 });

        if (_entries.Count > SweepThreshold)
        {
            foreach (var (key, e) in _entries)
                if (now - e.WindowStart >= Window)
                    _entries.TryRemove(key, out _);
        }
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private sealed record Entry(DateTimeOffset WindowStart, int Failures);
}

using System.Collections.Concurrent;

namespace WinSmtpRelay.AdminUi.Services;

/// <summary>Per-IP throttle for anonymous self-service signup, to limit abuse.</summary>
public interface ISignupRateLimiter
{
    /// <summary>
    /// Records an attempt from the client IP; returns false when more than <paramref name="maxPerHour"/>
    /// attempts have occurred in the last hour. A non-positive limit disables the throttle.
    /// </summary>
    bool TryRegister(string? clientIp, int maxPerHour);
}

public class SignupRateLimiter : ISignupRateLimiter
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _attempts = new();

    public bool TryRegister(string? clientIp, int maxPerHour)
    {
        if (maxPerHour <= 0)
            return true; // limit disabled

        var ip = string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp;
        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-1);
        var attempts = _attempts.GetOrAdd(ip, _ => []);

        lock (attempts)
        {
            attempts.RemoveAll(t => t < windowStart);
            if (attempts.Count >= maxPerHour)
                return false;
            attempts.Add(now);
            return true;
        }
    }
}

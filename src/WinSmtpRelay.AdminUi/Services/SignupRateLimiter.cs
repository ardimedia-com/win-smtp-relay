using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;

namespace WinSmtpRelay.AdminUi.Services;

/// <summary>Per-IP throttle for anonymous self-service signup, to limit abuse.</summary>
public interface ISignupRateLimiter
{
    /// <summary>Records an attempt from the client IP; returns false when the per-hour limit is exceeded.</summary>
    bool TryRegister(string? clientIp);
}

public class SignupRateLimiter(IOptions<AdminUiOptions> options) : ISignupRateLimiter
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _attempts = new();

    public bool TryRegister(string? clientIp)
    {
        var max = options.Value.SignupMaxAttemptsPerIpPerHour;
        if (max <= 0)
            return true; // limit disabled

        var ip = string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp;
        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-1);
        var attempts = _attempts.GetOrAdd(ip, _ => []);

        lock (attempts)
        {
            attempts.RemoveAll(t => t < windowStart);
            if (attempts.Count >= max)
                return false;
            attempts.Add(now);
            return true;
        }
    }
}

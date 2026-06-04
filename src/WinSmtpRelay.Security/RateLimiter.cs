using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Security;

public class RateLimiter
{
    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _userRecords = new();
    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _ipRecords = new();
    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _senderRecords = new();
    private readonly ConcurrentDictionary<string, FailedAuthRecord> _failedAuthRecords = new();
    private readonly IRuntimeConfigCache _cache;
    private readonly ILogger<RateLimiter> _logger;

    public RateLimiter(IRuntimeConfigCache cache, ILogger<RateLimiter> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public bool IsAllowed(string username, int? limitPerMinute, int? limitPerDay)
    {
        if (limitPerMinute is null && limitPerDay is null)
            return true;

        var record = _userRecords.GetOrAdd(username, _ => new SlidingWindowCounter());
        var now = DateTime.UtcNow;

        lock (record)
        {
            record.PruneOlderThan(now.AddDays(-1));

            if (limitPerMinute.HasValue && record.CountSince(now.AddMinutes(-1)) >= limitPerMinute.Value)
            {
                _logger.LogWarning("Rate limit exceeded for user {User}: per-minute limit {Limit}", username, limitPerMinute.Value);
                return false;
            }

            if (limitPerDay.HasValue && record.CountSince(now.AddDays(-1)) >= limitPerDay.Value)
            {
                _logger.LogWarning("Rate limit exceeded for user {User}: per-day limit {Limit}", username, limitPerDay.Value);
                return false;
            }

            record.Record(now);
            return true;
        }
    }

    public async Task<bool> IsIpAllowedAsync(string ipAddress, CancellationToken ct = default)
    {
        var settings = await _cache.GetRateLimitSettingsAsync(ct);
        if (settings.MaxConnectionsPerIpPerMinute <= 0) return true;

        var record = _ipRecords.GetOrAdd(ipAddress, _ => new SlidingWindowCounter());
        var now = DateTime.UtcNow;

        lock (record)
        {
            record.PruneOlderThan(now.AddMinutes(-5));

            if (record.CountSince(now.AddMinutes(-1)) >= settings.MaxConnectionsPerIpPerMinute)
            {
                _logger.LogWarning("IP rate limit exceeded for {Ip}: {Limit}/min", ipAddress, settings.MaxConnectionsPerIpPerMinute);
                return false;
            }

            record.Record(now);
            return true;
        }
    }

    public async Task<bool> IsSenderAllowedAsync(string senderAddress, CancellationToken ct = default)
    {
        var settings = await _cache.GetRateLimitSettingsAsync(ct);
        if (settings.MaxMessagesPerSenderPerMinute <= 0 && settings.MaxMessagesPerSenderPerDay <= 0)
            return true;

        var record = _senderRecords.GetOrAdd(senderAddress.ToLowerInvariant(), _ => new SlidingWindowCounter());
        var now = DateTime.UtcNow;

        lock (record)
        {
            record.PruneOlderThan(now.AddDays(-1));

            if (settings.MaxMessagesPerSenderPerMinute > 0 &&
                record.CountSince(now.AddMinutes(-1)) >= settings.MaxMessagesPerSenderPerMinute)
            {
                _logger.LogWarning("Sender rate limit exceeded for {Sender}: {Limit}/min", senderAddress, settings.MaxMessagesPerSenderPerMinute);
                return false;
            }

            if (settings.MaxMessagesPerSenderPerDay > 0 &&
                record.CountSince(now.AddDays(-1)) >= settings.MaxMessagesPerSenderPerDay)
            {
                _logger.LogWarning("Sender rate limit exceeded for {Sender}: {Limit}/day", senderAddress, settings.MaxMessagesPerSenderPerDay);
                return false;
            }

            record.Record(now);
            return true;
        }
    }

    public async Task RecordFailedAuthAsync(string ipAddress, CancellationToken ct = default)
    {
        var settings = await _cache.GetRateLimitSettingsAsync(ct);
        var record = _failedAuthRecords.GetOrAdd(ipAddress, _ => new FailedAuthRecord());
        lock (record)
        {
            record.FailCount++;
            record.LastFailUtc = DateTime.UtcNow;

            if (record.FailCount >= settings.FailedAuthBanThreshold)
            {
                record.BannedUntilUtc = DateTime.UtcNow.AddMinutes(settings.FailedAuthBanMinutes);
                _logger.LogWarning("IP {Ip} auto-banned for {Minutes} minutes after {Count} failed auth attempts",
                    ipAddress, settings.FailedAuthBanMinutes, record.FailCount);
            }
        }
    }

    public void ClearFailedAuth(string ipAddress)
    {
        _failedAuthRecords.TryRemove(ipAddress, out _);
    }

    public bool IsIpBanned(string ipAddress)
    {
        if (!_failedAuthRecords.TryGetValue(ipAddress, out var record))
            return false;

        lock (record)
        {
            if (record.BannedUntilUtc is null)
                return false;

            if (DateTime.UtcNow >= record.BannedUntilUtc.Value)
            {
                record.FailCount = 0;
                record.BannedUntilUtc = null;
                return false;
            }

            return true;
        }
    }

    private class SlidingWindowCounter
    {
        private readonly List<DateTime> _timestamps = [];

        public void Record(DateTime timestamp) => _timestamps.Add(timestamp);

        public int CountSince(DateTime since) =>
            _timestamps.Count(t => t >= since);

        public void PruneOlderThan(DateTime cutoff) =>
            _timestamps.RemoveAll(t => t < cutoff);
    }

    private class FailedAuthRecord
    {
        public int FailCount { get; set; }
        public DateTime LastFailUtc { get; set; }
        public DateTime? BannedUntilUtc { get; set; }
    }
}

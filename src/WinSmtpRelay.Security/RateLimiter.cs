using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Security;

/// <summary>
/// In-memory throttling for the SMTP hot path: per-IP command rate, per-sender and per-user recipient
/// budgets, and the failed-AUTH counters (per IP and per username).
/// <para>
/// Budgets count <em>recipients</em>, not MAIL commands: a check (<see cref="IsSenderWithinLimitAsync"/>,
/// <see cref="IsUserWithinLimit"/>) runs at MAIL and only asks whether the budget is already spent; every
/// accepted RCPT is then recorded (<see cref="RecordSenderRecipientsAsync"/>, <see cref="RecordUserRecipients"/>).
/// One message may therefore overshoot a budget by at most the per-message recipient cap, and the next
/// message is throttled until the window has room again.
/// </para>
/// <para>
/// Failed AUTH decays instead of being cleared: a success does not wipe the IP's counter (an attacker who
/// owns one valid credential could otherwise reset the counter between guesses); failures simply age out
/// of the window. Entries of every dictionary are evicted once their window has passed, so a scan from
/// many addresses cannot grow the process without bound.
/// </para>
/// </summary>
public class RateLimiter
{
    /// <summary>
    /// A username is locked after this many times the per-IP threshold of failures inside the ban window,
    /// counted across all client IPs — the defence against a distributed guess of one account. It is kept
    /// well above the per-IP threshold because a username lock is also a lever for a targeted denial of
    /// service against that account; the lock expires after the same ban period as an IP ban.
    /// </summary>
    public const int UsernameFailureMultiplier = 3;

    /// <summary>How often the dictionaries are swept for entries whose window has passed.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _userRecords = new();
    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _ipRecords = new();
    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _senderRecords = new();
    private readonly ConcurrentDictionary<string, FailedAuthRecord> _failedAuthByIp = new();
    private readonly ConcurrentDictionary<string, FailedAuthRecord> _failedAuthByUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly IRuntimeConfigCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<RateLimiter> _logger;
    private long _nextSweepTicks;

    public RateLimiter(IRuntimeConfigCache cache, ILogger<RateLimiter> logger, TimeProvider? timeProvider = null)
    {
        _cache = cache;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _nextSweepTicks = (_time.GetUtcNow() + SweepInterval).UtcTicks;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    // ---- Per-user recipient budget ----

    /// <summary>True while the user's budget still has room. Does not consume anything.</summary>
    public bool IsUserWithinLimit(string key, int? limitPerMinute, int? limitPerDay)
    {
        MaybeSweep();
        if (limitPerMinute is null && limitPerDay is null)
            return true;

        if (!_userRecords.TryGetValue(key, out var record))
            return true;

        var now = Now;
        lock (record)
        {
            record.PruneOlderThan(now.AddDays(-1));

            if (limitPerMinute.HasValue && record.CountSince(now.AddMinutes(-1)) >= limitPerMinute.Value)
            {
                _logger.LogWarning("Rate limit exceeded for {User}: per-minute limit {Limit}", key, limitPerMinute.Value);
                return false;
            }

            if (limitPerDay.HasValue && record.CountSince(now.AddDays(-1)) >= limitPerDay.Value)
            {
                _logger.LogWarning("Rate limit exceeded for {User}: per-day limit {Limit}", key, limitPerDay.Value);
                return false;
            }

            return true;
        }
    }

    /// <summary>Consumes <paramref name="recipients"/> units of the user's budget (one per accepted RCPT).</summary>
    public void RecordUserRecipients(string key, int? limitPerMinute, int? limitPerDay, int recipients = 1)
    {
        if ((limitPerMinute is null && limitPerDay is null) || recipients <= 0)
            return;

        var record = _userRecords.GetOrAdd(key, _ => new SlidingWindowCounter());
        var now = Now;
        lock (record)
            record.Record(now, recipients);
    }

    // ---- Per-IP command rate ----

    /// <summary>Counts one MAIL command for the client IP and returns false once the per-minute cap is hit.</summary>
    public async Task<bool> IsIpAllowedAsync(string ipAddress, CancellationToken ct = default)
    {
        MaybeSweep();
        var settings = await _cache.GetRateLimitSettingsAsync(ct);
        if (settings.MaxConnectionsPerIpPerMinute <= 0) return true;

        var record = _ipRecords.GetOrAdd(ipAddress, _ => new SlidingWindowCounter());
        var now = Now;

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

    // ---- Per-sender recipient budget ----

    /// <summary>True while the sender's budget still has room. Does not consume anything.</summary>
    public async Task<bool> IsSenderWithinLimitAsync(string senderAddress, CancellationToken ct = default)
    {
        MaybeSweep();
        var settings = await _cache.GetRateLimitSettingsAsync(ct);
        if (settings.MaxMessagesPerSenderPerMinute <= 0 && settings.MaxMessagesPerSenderPerDay <= 0)
            return true;

        if (!_senderRecords.TryGetValue(senderAddress.ToLowerInvariant(), out var record))
            return true;

        var now = Now;
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

            return true;
        }
    }

    /// <summary>Consumes <paramref name="recipients"/> units of the sender's budget (one per accepted RCPT).</summary>
    public async Task RecordSenderRecipientsAsync(string senderAddress, int recipients = 1, CancellationToken ct = default)
    {
        var settings = await _cache.GetRateLimitSettingsAsync(ct);
        if ((settings.MaxMessagesPerSenderPerMinute <= 0 && settings.MaxMessagesPerSenderPerDay <= 0) || recipients <= 0)
            return;

        var record = _senderRecords.GetOrAdd(senderAddress.ToLowerInvariant(), _ => new SlidingWindowCounter());
        var now = Now;
        lock (record)
            record.Record(now, recipients);
    }

    // ---- Failed AUTH ----

    /// <summary>
    /// Records one failed AUTH for the client IP and (when known) the username. The IP is banned once
    /// <c>FailedAuthBanThreshold</c> failures fall inside the ban window; the username is locked once
    /// <see cref="UsernameFailureMultiplier"/> times that many fall inside it, from any IP.
    /// </summary>
    public async Task RecordFailedAuthAsync(string? ipAddress, string? username = null, CancellationToken ct = default)
    {
        MaybeSweep();
        var settings = await _cache.GetRateLimitSettingsAsync(ct);
        if (settings.FailedAuthBanThreshold <= 0)
            return;

        var window = TimeSpan.FromMinutes(Math.Max(1, settings.FailedAuthBanMinutes));
        var now = Now;

        if (!string.IsNullOrEmpty(ipAddress))
        {
            var record = _failedAuthByIp.GetOrAdd(ipAddress, _ => new FailedAuthRecord());
            lock (record)
            {
                if (record.RecordFailure(now, window, settings.FailedAuthBanThreshold))
                    _logger.LogWarning("IP {Ip} auto-banned for {Minutes} minutes after {Count} failed auth attempts",
                        ipAddress, settings.FailedAuthBanMinutes, settings.FailedAuthBanThreshold);
            }
        }

        if (!string.IsNullOrEmpty(username))
        {
            var record = _failedAuthByUser.GetOrAdd(username, _ => new FailedAuthRecord());
            var threshold = settings.FailedAuthBanThreshold * UsernameFailureMultiplier;
            lock (record)
            {
                // The username is deliberately not logged here: a failed login's "username" is whatever the
                // client typed, which is not always a username (a password typed into the wrong field).
                if (record.RecordFailure(now, window, threshold))
                    _logger.LogWarning("SMTP AUTH temporarily locked for one username for {Minutes} minutes after {Count} failed attempts from any address",
                        settings.FailedAuthBanMinutes, threshold);
            }
        }
    }

    /// <summary>True while the IP is banned after repeated failed AUTH.</summary>
    public bool IsIpBanned(string ipAddress)
    {
        MaybeSweep();
        return _failedAuthByIp.TryGetValue(ipAddress, out var record) && IsLocked(record);
    }

    /// <summary>True while the username is locked after repeated failed AUTH from any address.</summary>
    public bool IsUsernameLocked(string username)
        => !string.IsNullOrEmpty(username) && _failedAuthByUser.TryGetValue(username, out var record) && IsLocked(record);

    private bool IsLocked(FailedAuthRecord record)
    {
        lock (record)
            return record.BannedUntil is { } until && Now < until;
    }

    // ---- Eviction ----

    /// <summary>Evicts every entry whose window has passed. Runs at most once per <see cref="SweepInterval"/>.</summary>
    private void MaybeSweep()
    {
        var now = Now;
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due)
            return;
        if (Interlocked.CompareExchange(ref _nextSweepTicks, (now + SweepInterval).UtcTicks, due) != due)
            return; // another thread sweeps

        Sweep(now);
    }

    /// <summary>Exposed for tests: evicts entries that no longer carry any state.</summary>
    internal void Sweep(DateTimeOffset now)
    {
        EvictCounters(_userRecords, now.AddDays(-1));
        EvictCounters(_senderRecords, now.AddDays(-1));
        EvictCounters(_ipRecords, now.AddMinutes(-5));
        EvictFailures(_failedAuthByIp, now);
        EvictFailures(_failedAuthByUser, now);
    }

    /// <summary>Number of tracked entries across all dictionaries (for tests and diagnostics).</summary>
    internal int TrackedEntryCount =>
        _userRecords.Count + _senderRecords.Count + _ipRecords.Count + _failedAuthByIp.Count + _failedAuthByUser.Count;

    private static void EvictCounters(ConcurrentDictionary<string, SlidingWindowCounter> records, DateTimeOffset cutoff)
    {
        foreach (var (key, record) in records)
        {
            lock (record)
            {
                record.PruneOlderThan(cutoff);
                if (!record.IsEmpty)
                    continue;
            }

            // Only remove the exact instance inspected — a concurrent GetOrAdd may have replaced it.
            records.TryRemove(new KeyValuePair<string, SlidingWindowCounter>(key, record));
        }
    }

    private static void EvictFailures(ConcurrentDictionary<string, FailedAuthRecord> records, DateTimeOffset now)
    {
        foreach (var (key, record) in records)
        {
            lock (record)
            {
                if (!record.IsExpired(now))
                    continue;
            }

            records.TryRemove(new KeyValuePair<string, FailedAuthRecord>(key, record));
        }
    }

    private sealed class SlidingWindowCounter
    {
        private readonly List<DateTimeOffset> _timestamps = [];

        public bool IsEmpty => _timestamps.Count == 0;

        public void Record(DateTimeOffset timestamp, int count = 1)
        {
            for (var i = 0; i < count; i++)
                _timestamps.Add(timestamp);
        }

        public int CountSince(DateTimeOffset since) =>
            _timestamps.Count(t => t >= since);

        public void PruneOlderThan(DateTimeOffset cutoff) =>
            _timestamps.RemoveAll(t => t < cutoff);
    }

    private sealed class FailedAuthRecord
    {
        private readonly List<DateTimeOffset> _failures = [];
        private TimeSpan _window;

        public DateTimeOffset? BannedUntil { get; private set; }

        /// <summary>Adds one failure; returns true when this failure starts a ban.</summary>
        public bool RecordFailure(DateTimeOffset now, TimeSpan window, int threshold)
        {
            _window = window;
            _failures.RemoveAll(t => t < now - window);
            _failures.Add(now);

            if (BannedUntil is { } until && now < until)
                return false; // already banned; a failure during a ban does not extend it

            if (_failures.Count < threshold)
                return false;

            BannedUntil = now + window;
            _failures.Clear();
            return true;
        }

        public bool IsExpired(DateTimeOffset now) =>
            (BannedUntil is null || now >= BannedUntil.Value)
            && _failures.TrueForAll(t => t < now - _window);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Singleton in-memory cache for runtime-editable configuration.
/// Loads from SQLite on first access, invalidated by Admin API on changes.
/// Thread-safe: uses SemaphoreSlim to prevent concurrent DB loads.
/// </summary>
public class RuntimeConfigCache : IRuntimeConfigCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RuntimeConfigCache> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private volatile IReadOnlyList<string>? _acceptedDomains;
    private volatile IReadOnlyList<string>? _acceptedSenderDomains;
    private volatile IReadOnlyDictionary<string, int>? _senderDomainOwners;
    private volatile IReadOnlyDictionary<string, int>? _recipientDomainOwners;
    private volatile IReadOnlyList<IpAccessRule>? _ipAccessRules;
    private volatile IReadOnlyList<DomainRoute>? _domainRoutes;
    private volatile IReadOnlyList<HeaderRewriteEntry>? _headerRewriteRules;
    private volatile IReadOnlyList<SenderRewriteEntry>? _senderRewriteRules;
    private volatile IReadOnlySet<int>? _enabledTenants;
    private volatile RateLimitSettings? _rateLimitSettings;

    public RuntimeConfigCache(IServiceScopeFactory scopeFactory, ILogger<RuntimeConfigCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetAcceptedDomainsAsync(CancellationToken ct = default)
    {
        if (_acceptedDomains is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_acceptedDomains is { } cached2)
                return cached2;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var domains = await db.AcceptedDomains
                .AsNoTracking()
                .Select(d => d.Domain)
                .ToListAsync(ct);

            _acceptedDomains = domains;
            _logger.LogDebug("Loaded {Count} accepted domains into cache", domains.Count);
            return domains;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetAcceptedSenderDomainsAsync(CancellationToken ct = default)
    {
        if (_acceptedSenderDomains is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_acceptedSenderDomains is { } cached2)
                return cached2;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var domains = await db.AcceptedSenderDomains
                .AsNoTracking()
                .Select(d => d.Domain)
                .ToListAsync(ct);

            _acceptedSenderDomains = domains;
            _logger.LogDebug("Loaded {Count} accepted sender domains into cache", domains.Count);
            return domains;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int?> GetTenantForSenderDomainAsync(string domain, CancellationToken ct = default)
    {
        var map = _senderDomainOwners ?? await LoadDomainOwnersAsync(sender: true, ct);
        return map.TryGetValue(domain, out var tenantId) ? tenantId : null;
    }

    public async Task<int?> GetTenantForRecipientDomainAsync(string domain, CancellationToken ct = default)
    {
        var map = _recipientDomainOwners ?? await LoadDomainOwnersAsync(sender: false, ct);
        return map.TryGetValue(domain, out var tenantId) ? tenantId : null;
    }

    private async Task<IReadOnlyDictionary<string, int>> LoadDomainOwnersAsync(bool sender, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (sender && _senderDomainOwners is { } s) return s;
            if (!sender && _recipientDomainOwners is { } r) return r;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var rows = sender
                ? await db.AcceptedSenderDomains.AsNoTracking().Select(d => new { d.Domain, d.TenantId }).ToListAsync(ct)
                : await db.AcceptedDomains.AsNoTracking().Select(d => new { d.Domain, d.TenantId }).ToListAsync(ct);

            // First-writer wins on the rare duplicate (same domain claimed by two tenants).
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
                map.TryAdd(row.Domain, row.TenantId);

            if (sender) _senderDomainOwners = map; else _recipientDomainOwners = map;
            return map;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsTenantEnabledAsync(int tenantId, CancellationToken ct = default)
    {
        var set = _enabledTenants;
        if (set is null)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (_enabledTenants is null)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
                    var ids = await db.Tenants
                        .AsNoTracking()
                        .Where(t => t.IsEnabled)
                        .Select(t => t.Id)
                        .ToListAsync(ct);
                    _enabledTenants = ids.ToHashSet();
                    _logger.LogDebug("Loaded {Count} enabled tenants into cache", ids.Count);
                }
                set = _enabledTenants;
            }
            finally
            {
                _lock.Release();
            }
        }

        return set.Contains(tenantId);
    }

    public async Task<RateLimitSettings> GetRateLimitSettingsAsync(CancellationToken ct = default)
    {
        if (_rateLimitSettings is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_rateLimitSettings is { } cached2)
                return cached2;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var settings = await db.RateLimitSettings.AsNoTracking().FirstOrDefaultAsync(ct)
                ?? new RateLimitSettings();

            _rateLimitSettings = settings;
            _logger.LogDebug("Loaded rate-limit settings into cache");
            return settings;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<IpAccessRule>> GetIpAccessRulesAsync(CancellationToken ct = default)
    {
        if (_ipAccessRules is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_ipAccessRules is { } cached2)
                return cached2;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var rules = await db.IpAccessRules
                .AsNoTracking()
                .OrderBy(r => r.SortOrder)
                .ToListAsync(ct);

            _ipAccessRules = rules;
            _logger.LogDebug("Loaded {Count} IP access rules into cache", rules.Count);
            return rules;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DomainRoute>> GetDomainRoutesAsync(CancellationToken ct = default)
    {
        if (_domainRoutes is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_domainRoutes is { } cached2)
                return cached2;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var routes = await db.DomainRoutes
                .AsNoTracking()
                .Include(r => r.SendConnector)
                .OrderBy(r => r.SortOrder)
                .ToListAsync(ct);

            _domainRoutes = routes;
            _logger.LogDebug("Loaded {Count} domain routes into cache", routes.Count);
            return routes;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<HeaderRewriteEntry>> GetHeaderRewriteRulesAsync(CancellationToken ct = default)
    {
        if (_headerRewriteRules is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_headerRewriteRules is { } cached2)
                return cached2;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var rules = await db.HeaderRewriteEntries
                .AsNoTracking()
                .Where(r => r.IsEnabled)
                .OrderBy(r => r.SortOrder)
                .ToListAsync(ct);

            _headerRewriteRules = rules;
            _logger.LogDebug("Loaded {Count} header rewrite rules into cache", rules.Count);
            return rules;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SenderRewriteEntry>> GetSenderRewriteRulesAsync(CancellationToken ct = default)
    {
        if (_senderRewriteRules is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_senderRewriteRules is { } cached2)
                return cached2;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var rules = await db.SenderRewriteEntries
                .AsNoTracking()
                .Where(r => r.IsEnabled)
                .OrderBy(r => r.SortOrder)
                .ToListAsync(ct);

            _senderRewriteRules = rules;
            _logger.LogDebug("Loaded {Count} sender rewrite rules into cache", rules.Count);
            return rules;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate()
    {
        _acceptedDomains = null;
        _acceptedSenderDomains = null;
        _senderDomainOwners = null;
        _recipientDomainOwners = null;
        _ipAccessRules = null;
        _domainRoutes = null;
        _headerRewriteRules = null;
        _senderRewriteRules = null;
        _enabledTenants = null;
        _rateLimitSettings = null;
        _logger.LogInformation("Runtime configuration cache invalidated");
    }
}

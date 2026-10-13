using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// API-key expiry: an enabled key that expires soon is a Warning, one that expires within the error window is
/// an Error (so the immediate alert fires once, days before callers start getting 401), and an enabled key
/// whose date has passed is a Warning — it is already refused, so it is either dead weight or a broken caller.
/// Disabled keys are ignored; keys without an expiry date never produce a finding.
/// </summary>
public sealed class ApiKeyHealthCheck(
    IApiKeyService apiKeys,
    ITenantService tenants,
    IOptions<HealthCheckOptions> options,
    ILogger<ApiKeyHealthCheck> logger) : HealthCheckBase
{
    public override string Name => "API keys";
    protected override string Category => HealthCategories.Security;

    public override async Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct)
    {
        var f = new List<HealthFinding>();
        var opts = options.Value;

        IReadOnlyList<ApiKey> keys;
        IReadOnlyDictionary<int, string> tenantNames;
        try
        {
            keys = await apiKeys.GetAllAsync(null, ct);
            tenantNames = (await tenants.GetAllAsync(ct)).ToDictionary(t => t.Id, t => t.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "loading API keys failed");
            return f;
        }

        var now = DateTimeOffset.UtcNow;
        var withExpiry = 0;
        foreach (var key in keys.Where(k => k.IsEnabled))
        {
            var state = ApiKeyExpiryPolicy.Evaluate(key.ExpiresUtc, now, opts.ApiKeyExpiryWarningDays, opts.ApiKeyExpiryErrorDays);
            if (state == ApiKeyExpiryState.NoExpiry)
                continue;
            withExpiry++;

            var expiresUtc = key.ExpiresUtc!.Value.UtcDateTime;
            var days = Math.Ceiling((key.ExpiresUtc.Value - now).TotalDays);
            var owner = key.TenantId is int id ? tenantNames.GetValueOrDefault(id, $"tenant {id}") : "host";
            var target = $"{key.KeyPrefix}… ({owner})";
            var label = $"API key '{key.Name}'";

            switch (state)
            {
                case ApiKeyExpiryState.Expired:
                    f.Add(Warn("api-key-expired", $"{label} has expired",
                        $"{label} ({target}) expired on {expiresUtc:yyyy-MM-dd HH:mm} UTC and is still enabled. Every call with it is refused (401).",
                        target, "Disable or delete it if it is no longer used; otherwise issue a new key and update the caller."));
                    break;
                case ApiKeyExpiryState.Imminent:
                    f.Add(Err("api-key-expiry", $"{label} expires in {days:F0} day(s)",
                        $"{label} ({target}) is valid until {expiresUtc:yyyy-MM-dd HH:mm} UTC. After that every call with it is refused (401).",
                        target, "Issue a replacement key now, update every caller (scripts, MCP configurations, secret stores), then disable this one."));
                    break;
                case ApiKeyExpiryState.Soon:
                    f.Add(Warn("api-key-expiry", $"{label} expires in {days:F0} day(s)",
                        $"{label} ({target}) is valid until {expiresUtc:yyyy-MM-dd HH:mm} UTC.",
                        target, "Plan the replacement: issue a new key, update every caller, then disable this one."));
                    break;
            }
        }

        if (withExpiry > 0 && f.Count == 0)
            f.Add(Ok("api-key-expiry", "No API key expires soon",
                $"{withExpiry} enabled key(s) carry an expiry date; none expires within {opts.ApiKeyExpiryWarningDays} days."));

        return f;
    }
}

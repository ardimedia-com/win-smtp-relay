namespace WinSmtpRelay.Core.Health;

/// <summary>Where an API key stands relative to its expiry date, as judged by the daily self-check.</summary>
public enum ApiKeyExpiryState
{
    /// <summary>The key has no expiry date.</summary>
    NoExpiry,

    /// <summary>The key expires later than the warning window.</summary>
    Valid,

    /// <summary>The key expires within the warning window.</summary>
    Soon,

    /// <summary>The key expires within the error window.</summary>
    Imminent,

    /// <summary>The expiry date has passed; the key is already refused.</summary>
    Expired,
}

/// <summary>
/// Classifies an API key's expiry for the self-check. Pure, so the thresholds are testable without a host:
/// <c>ApiKeyService.ValidateAsync</c> refuses a key once <c>ExpiresUtc &lt;= now</c>, and this policy warns
/// before that moment so a replacement can be issued and every caller updated in time.
/// </summary>
public static class ApiKeyExpiryPolicy
{
    public static ApiKeyExpiryState Evaluate(DateTimeOffset? expiresUtc, DateTimeOffset now, int warningDays, int errorDays)
    {
        if (expiresUtc is null)
            return ApiKeyExpiryState.NoExpiry;

        var days = (expiresUtc.Value - now).TotalDays;
        if (days <= 0)
            return ApiKeyExpiryState.Expired;
        if (days <= errorDays)
            return ApiKeyExpiryState.Imminent;
        if (days <= warningDays)
            return ApiKeyExpiryState.Soon;
        return ApiKeyExpiryState.Valid;
    }
}

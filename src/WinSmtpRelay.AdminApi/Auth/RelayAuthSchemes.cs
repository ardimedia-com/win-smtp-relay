namespace WinSmtpRelay.AdminApi.Auth;

public static class RelayAuthSchemes
{
    /// <summary>Policy scheme that forwards to the cookie scheme (browser) or the API-key scheme (automation) per request.</summary>
    public const string Smart = "RelaySmart";
}

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";

    /// <summary>Header carrying the raw API key. Callers may also use <c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    public const string HeaderName = "X-Api-Key";
}

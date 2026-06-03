using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// Authenticates programmatic callers via a hashed API key (X-Api-Key header or
/// Authorization: Bearer). Emits the same claims (role, tenant_id, is_host_admin)
/// as cookie login so authorization is uniform.
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyService apiKeys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ExtractKey();
        if (presented is null)
            return AuthenticateResult.NoResult();

        var key = await apiKeys.ValidateAsync(presented, Context.RequestAborted);
        if (key is null)
            return AuthenticateResult.Fail("Invalid or expired API key");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, $"apikey:{key.Id}"),
            new(ClaimTypes.Name, string.IsNullOrEmpty(key.Name) ? $"apikey:{key.Id}" : key.Name),
            new(ClaimTypes.Role, key.Role),
        };
        if (key.TenantId is not null)
            claims.Add(new Claim(RelayClaimTypes.TenantId, key.TenantId.Value.ToString()));
        if (key.Role == RelayRoles.HostAdmin)
            claims.Add(new Claim(RelayClaimTypes.IsHostAdmin, "true"));

        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    private string? ExtractKey()
    {
        if (Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var headerValues))
        {
            var value = headerValues.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        var auth = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            var token = auth[bearer.Length..].Trim();
            if (token.StartsWith("wsr_", StringComparison.Ordinal))
                return token;
        }

        return null;
    }
}

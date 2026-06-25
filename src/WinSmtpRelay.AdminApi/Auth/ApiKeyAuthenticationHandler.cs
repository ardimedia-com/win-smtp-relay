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
/// Authorization: Bearer). Emits the same membership claims as cookie login so authorization
/// (<see cref="RelayAccess"/>) and tenant scoping are uniform: a host-role key emits a host
/// membership; a tenant key emits a tenant membership scoped to its tenant.
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
        };
        if (key.Role == RelayRoles.HostAdmin)
        {
            // A host-role key is host-scoped (no active tenant) — host-level administration.
            claims.Add(new Claim(RelayClaimTypes.IsHostAdmin, "true"));
        }
        else if (key.TenantId is int tenantId)
        {
            // A tenant key carries a tenant membership and is pinned to its tenant scope.
            claims.Add(new Claim(RelayClaimTypes.TenantMembership, $"{tenantId}:{key.Role}"));
            claims.Add(new Claim(RelayClaimTypes.ActiveTenant, tenantId.ToString()));
        }

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

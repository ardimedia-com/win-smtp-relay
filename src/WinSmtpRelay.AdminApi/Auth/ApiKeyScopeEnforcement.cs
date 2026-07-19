using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using WinSmtpRelay.Core.Authorization;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// Endpoint metadata assigning an /api endpoint to one of the <see cref="ApiKeyScopes"/> areas.
/// The scope filter derives the required scope from it: <c>{area}:read</c> for GET/HEAD,
/// <c>{area}:write</c> otherwise — mirroring the group convention that auto-elevates non-GET
/// endpoints to AdminFull. An <paramref name="explicitScope"/> (e.g. <c>messages:body</c>) replaces
/// the method-derived requirement entirely.
/// </summary>
public sealed class ApiScopeMetadata(string area, string? explicitScope = null)
{
    public string Area { get; } = area;
    public string? ExplicitScope { get; } = explicitScope;
}

/// <summary>
/// Enforces API-key capability scopes on the /api group. Cookie-authenticated admins pass through
/// untouched — scopes are an additional restriction for programmatic callers only, layered on top of
/// (never replacing) the role policies. Fail-closed: an endpoint without <see cref="ApiScopeMetadata"/>
/// is not reachable with an API key at all, so a future endpoint added without classification cannot
/// silently widen a key's power.
/// </summary>
public static class ApiKeyScopeFilter
{
    public static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var user = http.User;

        // Only API-key principals carry the ApiKeyId claim; everyone else is governed by role alone.
        if (user.Identity?.IsAuthenticated != true || user.FindFirst(RelayClaimTypes.ApiKeyId) is null)
            return await next(context);

        var endpoint = http.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return await next(context);

        var scopes = ApiKeyScopes.Parse(user.FindFirst(RelayClaimTypes.ApiKeyScopes)?.Value);
        var meta = endpoint?.Metadata.GetMetadata<ApiScopeMetadata>();

        bool allowed;
        string required;
        if (meta is null)
        {
            allowed = false;
            required = "(endpoint not classified for API-key access)";
        }
        else if (meta.ExplicitScope is { } explicitScope)
        {
            required = explicitScope;
            allowed = scopes.Contains(explicitScope);
        }
        else if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
        {
            required = ApiKeyScopes.Read(meta.Area);
            allowed = ApiKeyScopes.AllowsRead(scopes, meta.Area);
        }
        else
        {
            required = ApiKeyScopes.Write(meta.Area);
            allowed = ApiKeyScopes.AllowsWrite(scopes, meta.Area);
        }

        if (!allowed)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "API key scope insufficient",
                detail: $"This API key does not carry the required scope '{required}'. "
                      + "A key without scopes is read-only; write scopes must be granted explicitly.");
        }

        return await next(context);
    }
}

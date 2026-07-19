using System.Security.Claims;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Core.Authorization;

/// <summary>
/// Maps an authenticated principal onto the ambient <see cref="ICurrentActor"/> — applied at the same
/// two points as <see cref="TenantContextResolver"/> (the HTTP middleware and the Blazor circuit
/// handler), so wherever a tenant scope exists, the acting admin is known too and service-level audit
/// rows can name who acted.
/// </summary>
public static class ActorContextResolver
{
    public static void Apply(ClaimsPrincipal user, ICurrentActor current)
    {
        if (user.Identity?.IsAuthenticated != true)
            return;

        // An API-key principal carries its key id in a dedicated claim (its NameIdentifier is the
        // non-numeric "apikey:{id}"), so programmatic actors stay attributable in the audit trail
        // instead of collapsing to a null actor.
        var apiKeyId = int.TryParse(user.FindFirst(RelayClaimTypes.ApiKeyId)?.Value, out var kid) ? kid : (int?)null;
        var userId = int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : (int?)null;
        current.Set(userId, user.Identity.Name, apiKeyId);
    }
}

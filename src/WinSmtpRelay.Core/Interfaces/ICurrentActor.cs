namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// The human (admin) on whose behalf the current scope is acting — the audit-trail counterpart of
/// <see cref="ICurrentTenant"/>, and plumbed through the same three points: the HTTP middleware sets it
/// per request, the Blazor circuit handler per circuit, and <c>TenantScopeFactory</c> clones it into
/// per-operation child scopes.
/// <para>
/// It exists so the storage services can audit their own mutations at the source instead of every
/// caller remembering to — the same forget-proofing argument as the runtime-config-cache invalidation.
/// Unset means no human is acting: a background job, the SMTP listener, or startup seeding. Audit rows
/// written from such a scope carry a null actor, which is the honest record.
/// </para>
/// </summary>
public interface ICurrentActor
{
    /// <summary>The acting admin's user id, or null when no human is acting (background/system scope).</summary>
    int? UserId { get; }

    /// <summary>The API key acting, when the caller authenticated with one (automation, MCP) instead
    /// of a signed-in admin. Mutually exclusive with <see cref="UserId"/>.</summary>
    int? ApiKeyId { get; }

    /// <summary>The acting admin's sign-in name/email (or the API key's name), for the audit row's
    /// readable actor column.</summary>
    string? Email { get; }

    void Set(int? userId, string? email, int? apiKeyId = null);
}

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Ambient tenant for the current scope. Set per request (from the admin's claims) or per
/// Blazor circuit. Left unset in background/host scopes, in which case no tenant filter is
/// applied (the caller sees all tenants). Scoped lifetime.
/// </summary>
public interface ICurrentTenant
{
    /// <summary>The resolved tenant id, or null for host/background scope.</summary>
    int? TenantId { get; }

    /// <summary>True for a host-level principal that intentionally sees all tenants.</summary>
    bool IsHostScope { get; }

    /// <summary>Whether the tenant query filter should apply in this scope.</summary>
    bool FilterEnabled { get; }

    /// <summary>Non-nullable tenant id used by the EF query filter (0 when no filter applies).</summary>
    int FilterTenantId { get; }

    /// <summary>Scope all data access to a single tenant.</summary>
    void SetTenant(int tenantId);

    /// <summary>Disable filtering for this scope (host-level: sees all tenants).</summary>
    void SetHostScope();
}

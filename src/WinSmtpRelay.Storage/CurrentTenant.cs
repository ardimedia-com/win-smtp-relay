using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Mutable, scope-lifetime holder for the ambient tenant. Default state (never set) applies
/// no filter, which is correct for background services that operate across all tenants.
/// </summary>
public class CurrentTenant : ICurrentTenant
{
    public int? TenantId { get; private set; }
    public bool IsHostScope { get; private set; }

    public bool FilterEnabled => TenantId.HasValue && !IsHostScope;
    public int FilterTenantId => TenantId ?? 0;

    public void SetTenant(int tenantId)
    {
        TenantId = tenantId;
        IsHostScope = false;
    }

    public void SetHostScope()
    {
        IsHostScope = true;
    }
}

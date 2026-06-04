using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Creates DI scopes that inherit the current scope's tenant. Blazor pages use this instead of
/// <see cref="IServiceScopeFactory"/> so per-operation DbContext scopes stay tenant-scoped (a raw
/// child scope would otherwise get a fresh, unset <see cref="ICurrentTenant"/> and see all tenants).
/// </summary>
public interface ITenantScopeFactory
{
    IServiceScope CreateScope();
}

public class TenantScopeFactory(IServiceScopeFactory inner, ICurrentTenant current) : ITenantScopeFactory
{
    public IServiceScope CreateScope()
    {
        var scope = inner.CreateScope();
        var child = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        if (current.IsHostScope)
            child.SetHostScope();
        else if (current.TenantId is { } id)
            child.SetTenant(id);
        return scope;
    }
}

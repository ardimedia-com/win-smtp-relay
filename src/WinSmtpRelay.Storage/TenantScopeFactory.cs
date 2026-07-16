using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Creates DI scopes that inherit the current scope's ambient context — the tenant AND the acting
/// admin. Blazor pages use this instead of <see cref="IServiceScopeFactory"/> so per-operation
/// DbContext scopes stay tenant-scoped and audit rows keep their actor (a raw child scope would
/// otherwise get a fresh, unset <see cref="ICurrentTenant"/>/<see cref="ICurrentActor"/> — seeing all
/// tenants and auditing as "system").
/// </summary>
public interface ITenantScopeFactory
{
    IServiceScope CreateScope();
}

public class TenantScopeFactory(
    IServiceScopeFactory inner,
    ICurrentTenant current,
    ICurrentActor currentActor) : ITenantScopeFactory
{
    public IServiceScope CreateScope()
    {
        var scope = inner.CreateScope();
        var child = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        if (current.IsHostScope)
            child.SetHostScope();
        else if (current.TenantId is { } id)
            child.SetTenant(id);

        scope.ServiceProvider.GetRequiredService<ICurrentActor>().Set(currentActor.UserId, currentActor.Email);
        return scope;
    }
}

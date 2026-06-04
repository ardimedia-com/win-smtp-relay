using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface ITenantService
{
    Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Tenant?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<Tenant?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Creates a tenant. Throws <see cref="InvalidOperationException"/> if the slug is taken.</summary>
    Task<Tenant> CreateAsync(string name, string slug, CancellationToken cancellationToken = default);

    Task UpdateAsync(int id, string name, bool isEnabled, CancellationToken cancellationToken = default);

    /// <summary>Deletes a tenant. The default tenant cannot be deleted; deletion fails if the tenant still owns data.</summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}

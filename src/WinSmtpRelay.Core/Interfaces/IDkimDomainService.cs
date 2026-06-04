using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IDkimDomainService
{
    Task<IReadOnlyList<DkimDomain>> GetAllAsync(CancellationToken ct = default);
    Task<DkimDomain?> GetByDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>The enabled DKIM key for a specific tenant + sender domain (explicit tenant filter, used by the delivery signer to prevent cross-tenant signing).</summary>
    Task<DkimDomain?> GetForSigningAsync(int tenantId, string domain, CancellationToken ct = default);
    Task<DkimDomain> CreateAsync(DkimDomain dkim, CancellationToken ct = default);
    Task UpdateAsync(DkimDomain dkim, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

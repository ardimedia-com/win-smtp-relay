using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Delivery.Tests;

internal class StubDkimDomainService : IDkimDomainService
{
    public Task<IReadOnlyList<DkimDomain>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DkimDomain>>([]);

    public Task<DkimDomain?> GetByDomainAsync(string domain, CancellationToken ct = default)
        => Task.FromResult<DkimDomain?>(null);

    public Task<DkimDomain?> GetForSigningAsync(int tenantId, string domain, CancellationToken ct = default)
        => Task.FromResult<DkimDomain?>(null);

    public Task<DkimDomain> CreateAsync(DkimDomain dkim, CancellationToken ct = default)
        => Task.FromResult(dkim);

    public Task UpdateAsync(DkimDomain dkim, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
}

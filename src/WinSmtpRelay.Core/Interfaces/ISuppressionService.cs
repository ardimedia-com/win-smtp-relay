using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Per-tenant suppression list of recipient addresses the relay must not deliver to. The
/// tenant-scoped methods (<see cref="GetAllAsync"/>, <see cref="RemoveAsync"/>) follow the ambient
/// <see cref="ICurrentTenant"/> scope like other admin services; the delivery-path methods
/// (<see cref="IsSuppressedAsync"/>, <see cref="AddAsync"/>) take an explicit tenant id because they
/// run in the unscoped background delivery worker.
/// </summary>
public interface ISuppressionService
{
    /// <summary>True if <paramref name="address"/> is suppressed for the given tenant.</summary>
    Task<bool> IsSuppressedAsync(string address, int tenantId, CancellationToken ct = default);

    /// <summary>Adds (or no-ops if already present) a suppressed address for the given tenant.</summary>
    Task AddAsync(string address, SuppressionReason reason, string? detail, int tenantId, CancellationToken ct = default);

    /// <summary>Lists suppressed addresses in the current tenant scope (most recent first).</summary>
    Task<IReadOnlyList<SuppressionEntry>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Removes a suppression entry by id, within the current tenant scope.</summary>
    Task RemoveAsync(int id, CancellationToken ct = default);
}

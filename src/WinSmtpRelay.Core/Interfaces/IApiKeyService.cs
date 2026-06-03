using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IApiKeyService
{
    /// <summary>Lists keys, optionally scoped to a tenant. Pass null for all (host view).</summary>
    Task<IReadOnlyList<ApiKey>> GetAllAsync(int? tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a key and returns the stored entity plus the one-time plaintext value.
    /// The plaintext is never persisted and cannot be recovered later.
    /// </summary>
    Task<(ApiKey Key, string Plaintext)> CreateAsync(
        int? tenantId, string name, string role, DateTime? expiresUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Validates a presented key. Returns the matching enabled, unexpired key (and updates
    /// LastUsedUtc), or null if invalid. Comparison is hash-based and timing-safe.
    /// </summary>
    Task<ApiKey?> ValidateAsync(string presentedKey, CancellationToken cancellationToken);

    Task DeleteAsync(int id, CancellationToken cancellationToken);
}

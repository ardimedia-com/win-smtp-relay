using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IUserService
{
    Task<bool> ValidateCredentialsAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates credentials and returns the matching enabled user, or null. Resolves duplicate
    /// usernames across tenants deterministically by the password (usernames are unique only per
    /// tenant), so the caller can bind the session to the correct tenant.
    /// </summary>
    Task<RelayUser?> ValidateAndGetAsync(string username, string password, CancellationToken cancellationToken = default);

    Task<RelayUser?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a user by username within a specific tenant. Usernames are unique only per tenant, so
    /// the SMTP path must qualify by the authenticated session's tenant — looking up by username alone
    /// can return another tenant's same-named user (wrong SendAs allow-list / rate limits).
    /// </summary>
    Task<RelayUser?> GetByUsernameAsync(string username, int tenantId, CancellationToken cancellationToken = default);
    Task CreateUserAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelayUser>> GetAllUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates a relay user's enablement, SendAs allow-list and rate limits (the mutable,
    /// non-credential part) — routed through the service so the change is audited.</summary>
    Task UpdateUserAsync(int userId, bool isEnabled, string? allowedSenderAddresses,
        int? rateLimitPerMinute, int? rateLimitPerDay, CancellationToken cancellationToken = default);

    Task DeleteUserAsync(int userId, CancellationToken cancellationToken = default);
}

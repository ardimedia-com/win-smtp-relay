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
    Task CreateUserAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelayUser>> GetAllUsersAsync(CancellationToken cancellationToken = default);
    Task DeleteUserAsync(int userId, CancellationToken cancellationToken = default);
}

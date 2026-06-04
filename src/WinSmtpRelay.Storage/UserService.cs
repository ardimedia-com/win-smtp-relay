using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class UserService(RelayDbContext db) : IUserService
{
    public async Task<bool> ValidateCredentialsAsync(string username, string password, CancellationToken cancellationToken = default)
        => await ValidateAndGetAsync(username, password, cancellationToken) is not null;

    public async Task<RelayUser?> ValidateAndGetAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        // The same username can exist in multiple tenants (unique index is (TenantId, Username)),
        // so load all enabled candidates and let the password select the right one.
        var candidates = await db.RelayUsers
            .AsNoTracking()
            .Where(u => u.Username == username && u.IsEnabled)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(u => BCrypt.Net.BCrypt.Verify(password, u.PasswordHash));
    }

    public async Task<RelayUser?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        // FirstOrDefault (not Single): usernames are unique only per tenant now.
        return await db.RelayUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
    }

    public async Task CreateUserAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);

        db.RelayUsers.Add(new RelayUser
        {
            Username = username,
            PasswordHash = hash
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RelayUser>> GetAllUsersAsync(CancellationToken cancellationToken = default)
    {
        return await db.RelayUsers.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task DeleteUserAsync(int userId, CancellationToken cancellationToken = default)
    {
        await db.RelayUsers.Where(u => u.Id == userId).ExecuteDeleteAsync(cancellationToken);
    }
}

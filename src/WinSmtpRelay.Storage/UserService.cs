using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// Relay-user lifecycle (SMTP credentials, SendAs allow-list, rate limits) is audited at the SERVICE
// so no caller can mint or alter a sending credential without leaving a trace.
public class UserService(
    RelayDbContext db,
    ICurrentActor actor,
    IAdminAuditService audit) : IUserService
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

    public async Task<RelayUser?> GetByUsernameAsync(string username, int tenantId, CancellationToken cancellationToken = default)
    {
        // Explicitly tenant-qualified (the SMTP path resolves this in a raw scope where the tenant
        // query filter is off), so two tenants sharing a username never cross over.
        return await db.RelayUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username && u.TenantId == tenantId, cancellationToken);
    }

    public async Task CreateUserAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);

        var user = new RelayUser
        {
            Username = username,
            PasswordHash = hash
        };
        db.RelayUsers.Add(user);

        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(AdminAuditActions.RelayUserCreated, actor, tenantId: user.TenantId,
            detail: username, ct: cancellationToken);
    }

    public async Task<IReadOnlyList<RelayUser>> GetAllUsersAsync(CancellationToken cancellationToken = default)
    {
        return await db.RelayUsers.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task UpdateUserAsync(int userId, bool isEnabled, string? allowedSenderAddresses,
        int? rateLimitPerMinute, int? rateLimitPerDay, CancellationToken cancellationToken = default)
    {
        var user = await db.RelayUsers.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return;

        user.IsEnabled = isEnabled;
        user.AllowedSenderAddresses = allowedSenderAddresses;
        user.RateLimitPerMinute = rateLimitPerMinute;
        user.RateLimitPerDay = rateLimitPerDay;

        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(AdminAuditActions.RelayUserUpdated, actor, tenantId: user.TenantId,
            detail: $"{user.Username} enabled={user.IsEnabled}", ct: cancellationToken);
    }

    public async Task DeleteUserAsync(int userId, CancellationToken cancellationToken = default)
    {
        // Load-then-delete so the audit row can name the user that was removed.
        var user = await db.RelayUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return;

        await db.RelayUsers.Where(u => u.Id == userId).ExecuteDeleteAsync(cancellationToken);
        await audit.WriteAsync(AdminAuditActions.RelayUserDeleted, actor, tenantId: user.TenantId,
            detail: user.Username, ct: cancellationToken);
    }
}

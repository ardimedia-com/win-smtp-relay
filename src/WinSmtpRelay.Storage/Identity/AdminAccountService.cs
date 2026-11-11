using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Storage.Identity;

/// <summary>Outcome of an <see cref="IAdminAccountService"/> operation; <see cref="Error"/> is operator-facing.</summary>
public sealed record AdminAccountResult(bool Succeeded, string? Error = null)
{
    public static readonly AdminAccountResult Ok = new(true);
    public static AdminAccountResult Fail(string error) => new(false, error);
}

/// <summary>
/// Account lifecycle actions on web-admin accounts — disable, enable, unlock, self-service password reset — with
/// the session and audit consequences in ONE place, so no page can disable an account and leave its session
/// running. Two states both surface as "locked out" and are kept apart deliberately:
/// <list type="bullet">
///   <item><b>Disabled</b> — <c>LockoutEnd = DateTimeOffset.MaxValue</c>, set by an administrator (or a pending
///   self-service signup). Never expires; only <see cref="EnableAsync"/> undoes it.</item>
///   <item><b>Brute-force lockout</b> — <c>LockoutEnd = now + 15 min</c>, set by failed sign-ins. Expires on its
///   own; <see cref="UnlockAsync"/> or a successful password reset clears it at once.</item>
/// </list>
/// </summary>
public interface IAdminAccountService
{
    /// <summary>Disables the account and ends its sessions (security stamp rotated). Refuses self and the last enabled host admin.</summary>
    Task<AdminAccountResult> DisableAsync(int targetUserId, int actorUserId, string? actorEmail, CancellationToken ct = default);

    /// <summary>Re-enables a disabled account (also clears a brute-force lockout and the failed-attempt counter).</summary>
    Task<AdminAccountResult> EnableAsync(int targetUserId, int actorUserId, string? actorEmail, CancellationToken ct = default);

    /// <summary>Clears a brute-force lockout immediately. Refuses a disabled account — that needs <see cref="EnableAsync"/>.</summary>
    Task<AdminAccountResult> UnlockAsync(int targetUserId, int actorUserId, string? actorEmail, CancellationToken ct = default);

    /// <summary>
    /// Self-service reset from the mailed link: sets the new password (which rotates the stamp), clears a
    /// brute-force lockout — never an administrative disable — and writes the audit row.
    /// </summary>
    Task<AdminAccountResult> ResetPasswordWithTokenAsync(int userId, string token, string newPassword, CancellationToken ct = default);
}

public class AdminAccountService(
    UserManager<AdminUser> userManager,
    IAdminMembershipService memberships,
    IAdminAuditService audit,
    ILogger<AdminAccountService> logger) : IAdminAccountService
{
    /// <summary>True when an administrator disabled the account (as opposed to a self-expiring brute-force lockout).</summary>
    public static bool IsDisabled(AdminUser user) => user.LockoutEnd is { } end && end.UtcDateTime.Year >= 9999;

    /// <summary>True while a brute-force lockout is running (not an administrative disable).</summary>
    public static bool IsBruteForceLocked(AdminUser user, DateTimeOffset now) =>
        !IsDisabled(user) && user.LockoutEnd is { } end && end > now;

    public async Task<AdminAccountResult> DisableAsync(int targetUserId, int actorUserId, string? actorEmail, CancellationToken ct = default)
    {
        if (targetUserId == actorUserId)
            return AdminAccountResult.Fail("You can't disable your own account.");
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
            return AdminAccountResult.Fail("The account no longer exists.");
        if (IsDisabled(user))
            return AdminAccountResult.Ok;

        var isHostAdmin = (await memberships.GetForUserAsync(user.Id, ct)).Any(m => m.TenantId is null);
        if (isHostAdmin && await memberships.CountHostAdminsAsync(ct) <= 1)
            return AdminAccountResult.Fail("You can't disable the last enabled host admin.");

        await userManager.SetLockoutEnabledAsync(user, true);
        var result = await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        if (!result.Succeeded)
            return AdminAccountResult.Fail(Describe(result));
        // The lockout alone is checked only at sign-in; rotating the stamp is what ends the running sessions
        // (cookie re-validation and the Blazor circuit's revalidating state provider both compare it).
        await userManager.UpdateSecurityStampAsync(user);
        await audit.WriteAsync(AdminAuditActions.AdminDisabled, actorUserId, actorEmail, targetUserId: user.Id, ct: ct);
        logger.LogInformation("Admin account {TargetUserId} disabled by {ActorUserId}; its sessions end at the next validation",
            user.Id, actorUserId);
        return AdminAccountResult.Ok;
    }

    public async Task<AdminAccountResult> EnableAsync(int targetUserId, int actorUserId, string? actorEmail, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
            return AdminAccountResult.Fail("The account no longer exists.");

        var result = await userManager.SetLockoutEndDateAsync(user, null);
        if (!result.Succeeded)
            return AdminAccountResult.Fail(Describe(result));
        await userManager.ResetAccessFailedCountAsync(user);
        await audit.WriteAsync(AdminAuditActions.AdminEnabled, actorUserId, actorEmail, targetUserId: user.Id, ct: ct);
        return AdminAccountResult.Ok;
    }

    public async Task<AdminAccountResult> UnlockAsync(int targetUserId, int actorUserId, string? actorEmail, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
            return AdminAccountResult.Fail("The account no longer exists.");
        if (IsDisabled(user))
            return AdminAccountResult.Fail("The account is disabled, not locked — enable it instead.");

        await ClearBruteForceLockoutAsync(user);
        await audit.WriteAsync(AdminAuditActions.AdminUnlocked, actorUserId, actorEmail, targetUserId: user.Id, ct: ct);
        return AdminAccountResult.Ok;
    }

    public async Task<AdminAccountResult> ResetPasswordWithTokenAsync(int userId, string token, string newPassword, CancellationToken ct = default)
    {
        const string invalidLink = "This reset link is invalid or has expired.";
        if (userId <= 0 || string.IsNullOrEmpty(token))
            return AdminAccountResult.Fail(invalidLink);
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return AdminAccountResult.Fail(invalidLink);

        // ResetPasswordAsync verifies the single-use token and rotates the security stamp (which ends every other
        // session and outstanding link of the account).
        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
            return AdminAccountResult.Fail(Describe(result));

        // A user who forgot the password usually also locked the account by guessing: the reset proves control of
        // the mailbox, so it lifts the brute-force lockout at once. An administrative disable stays — a disabled
        // user must not be able to re-enable themselves through the reset link.
        var hadLockout = IsBruteForceLocked(user, DateTimeOffset.UtcNow);
        if (!IsDisabled(user))
            await ClearBruteForceLockoutAsync(user);

        if (user.MustChangePassword)
        {
            user.MustChangePassword = false;
            await userManager.UpdateAsync(user);
        }

        await audit.WriteAsync(AdminAuditActions.AdminPasswordReset, user.Id, user.Email, targetUserId: user.Id,
            detail: hadLockout ? "self-service reset link (brute-force lockout cleared)" : "self-service reset link", ct: ct);
        logger.LogInformation("Password reset completed for admin {UserId} via reset link", user.Id);
        return AdminAccountResult.Ok;
    }

    private async Task ClearBruteForceLockoutAsync(AdminUser user)
    {
        if (user.LockoutEnd is not null)
            await userManager.SetLockoutEndDateAsync(user, null);
        if (user.AccessFailedCount > 0)
            await userManager.ResetAccessFailedCountAsync(user);
    }

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));
}

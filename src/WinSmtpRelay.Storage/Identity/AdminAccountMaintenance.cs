using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WinSmtpRelay.Storage.Identity;

/// <summary>
/// Startup maintenance for administrator accounts. There is no auto-seeded default administrator anymore:
/// the first administrator is created by the operator through first-run setup (<c>/account/initial-setup</c>).
/// These helpers retire the legacy seeded <c>admin@local</c> account and provide a break-glass full reset, so
/// that on a fresh — or reset — install no account exists and first-run setup must define one.
/// </summary>
public static class AdminAccountMaintenance
{
    /// <summary>
    /// Deletes the legacy seeded administrator (<paramref name="legacyEmail"/>, historically
    /// <c>admin@local</c>) <b>only when it is the sole account</b>, so that a real administrator must be
    /// defined through first-run setup. A no-op when no users, more than one user, or a single non-legacy
    /// user exists. Returns <c>true</c> if it deleted the account.
    /// </summary>
    public static async Task<bool> DeleteLoneLegacyAdminAsync(
        UserManager<AdminUser> userManager, string legacyEmail, ILogger logger, CancellationToken ct = default)
    {
        if (await userManager.Users.CountAsync(ct) != 1)
            return false;

        var only = await userManager.Users.SingleAsync(ct);
        if (!string.Equals(only.UserName, legacyEmail, StringComparison.OrdinalIgnoreCase))
            return false;

        var result = await userManager.DeleteAsync(only);
        if (result.Succeeded)
        {
            logger.LogWarning(
                "Removed the legacy seeded administrator '{Email}' (it was the only account). " +
                "Complete first-run setup at /account/initial-setup to define a new administrator.", legacyEmail);
            return true;
        }

        logger.LogError("Failed to remove the legacy seeded administrator '{Email}': {Errors}",
            legacyEmail, string.Join("; ", result.Errors.Select(e => e.Description)));
        return false;
    }

    /// <summary>
    /// Removes <b>every</b> administrator account (break-glass, e.g. the installer's "reset administrator
    /// access" option) so the operator re-defines one through first-run setup. Returns the number removed.
    /// </summary>
    public static async Task<int> DeleteAllAdminsAsync(
        UserManager<AdminUser> userManager, ILogger logger, CancellationToken ct = default)
    {
        var users = await userManager.Users.ToListAsync(ct);
        var removed = 0;
        foreach (var user in users)
        {
            var result = await userManager.DeleteAsync(user);
            if (result.Succeeded)
                removed++;
            else
                logger.LogError("Failed to remove administrator '{Email}' during reset: {Errors}",
                    user.UserName, string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        if (removed > 0)
            logger.LogWarning(
                "Administrator reset: removed {Count} administrator account(s). " +
                "Complete first-run setup at /account/initial-setup to define a new administrator.", removed);

        return removed;
    }
}

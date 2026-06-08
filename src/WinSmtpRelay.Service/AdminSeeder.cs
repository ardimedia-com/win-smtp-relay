using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Service;

/// <summary>
/// On startup ensures the admin roles and default tenant exist, and seeds an initial
/// host administrator (admin@local) with a one-time random password if no admin exists.
/// The password is logged once (Event Log + console); the account must change it on first use.
/// </summary>
public class AdminSeeder(IServiceScopeFactory scopeFactory, ILogger<AdminSeeder> logger) : IHostedService
{
    private const string InitialAdminEmail = AdminBootstrap.InitialAdminEmail;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<RelayDbContext>();
        var roleManager = sp.GetRequiredService<RoleManager<AdminRole>>();
        var userManager = sp.GetRequiredService<UserManager<AdminUser>>();

        // Roles
        foreach (var role in RelayRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new AdminRole(role));
        }

        // Default tenant (also seeded by migration; ensure for safety)
        if (!await db.Tenants.AnyAsync(t => t.Id == TenantDefaults.DefaultTenantId, cancellationToken))
        {
            db.Tenants.Add(new Tenant
            {
                Id = TenantDefaults.DefaultTenantId,
                Name = TenantDefaults.DefaultName,
                Slug = TenantDefaults.DefaultSlug,
                IsEnabled = true,
                CreatedUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        // Initial host admin: seed when none exists; or, if the installer set the reset flag (lost
        // password recovery), regenerate the admin@local password — same flow as a fresh install.
        var resetRequested = ReadAndClearResetFlag();

        if (!await userManager.Users.AnyAsync(cancellationToken))
        {
            var password = GenerateStrongPassword();
            var admin = new AdminUser
            {
                UserName = InitialAdminEmail,
                Email = InitialAdminEmail,
                EmailConfirmed = true,
                IsHostAdmin = true,
                TenantId = null,
                DisplayName = "Administrator",
                MustChangePassword = true,
                CreatedUtc = DateTimeOffset.UtcNow
            };

            var result = await userManager.CreateAsync(admin, password);
            if (!result.Succeeded)
            {
                logger.LogError("Failed to seed initial admin account: {Errors}",
                    string.Join("; ", result.Errors.Select(e => e.Description)));
                return;
            }

            await userManager.AddToRoleAsync(admin, RelayRoles.HostAdmin);
            LogInitialPassword("CREATED", password);
            TryWritePasswordFile(password);
            return;
        }

        if (resetRequested)
            await ResetInitialAdminPasswordAsync(userManager);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Regenerates the admin@local password (lost-password recovery requested via the installer). Same
    // outcome as a fresh seed: a new one-time password, written to the file + log, must be changed on
    // next sign-in. Also clears any lockout so a locked-out admin can get back in.
    private async Task ResetInitialAdminPasswordAsync(UserManager<AdminUser> userManager)
    {
        var admin = await userManager.FindByNameAsync(InitialAdminEmail);
        if (admin is null)
        {
            logger.LogWarning("Admin password reset was requested, but '{Email}' does not exist — nothing to reset.", InitialAdminEmail);
            return;
        }

        var password = GenerateStrongPassword();
        var token = await userManager.GeneratePasswordResetTokenAsync(admin);
        var result = await userManager.ResetPasswordAsync(admin, token, password);
        if (!result.Succeeded)
        {
            logger.LogError("Failed to reset the admin password: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        admin.MustChangePassword = true;
        await userManager.SetLockoutEndDateAsync(admin, null);
        await userManager.ResetAccessFailedCountAsync(admin);
        await userManager.UpdateAsync(admin);

        LogInitialPassword("RESET", password);
        TryWritePasswordFile(password);
    }

    private void LogInitialPassword(string action, string password) =>
        logger.LogWarning(
            "================ INITIAL ADMIN ACCOUNT {Action} ================\n" +
            "  Sign in at the admin UI with:\n" +
            "    Username: {Email}\n" +
            "    Password: {Password}\n" +
            "  CHANGE THIS PASSWORD IMMEDIATELY at /account/change-password.\n" +
            "  This password is shown only once.\n" +
            "==============================================================",
            action, InitialAdminEmail, password);

    // Reads (and clears) the installer-set reset flag HKLM\SOFTWARE\ARDIMEDIA\WinSmtpRelay\ResetAdminPassword.
    // NetworkService has write access to this key (granted by the installer), so it can clear the flag.
    private bool ReadAndClearResetFlag()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\ARDIMEDIA\WinSmtpRelay", writable: true);
            if (key?.GetValue("ResetAdminPassword") is int flag && flag == 1)
            {
                key.DeleteValue("ResetAdminPassword", throwOnMissingValue: false);
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read/clear the admin password-reset flag.");
        }
        return false;
    }

    // Writes the one-time password to a file next to the service binaries so the operator can grab it
    // without digging through the Event Log. The sign-in page links to it, and it is deleted automatically
    // on the first password change. Best-effort: the password is also in the log above.
    private void TryWritePasswordFile(string password)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, AdminBootstrap.PasswordFileName);
            var content =
                "WIN-SMTP-RELAY — initial administrator account\r\n" +
                "==============================================\r\n\r\n" +
                $"  Username:  {InitialAdminEmail}\r\n" +
                $"  Password:  {password}\r\n\r\n" +
                "Sign in to the admin UI and change this password immediately (you will be prompted).\r\n" +
                "This file is deleted automatically once the password is changed.\r\n" +
                "If you do not sign in, delete this file yourself — it contains a valid password.\r\n";
            File.WriteAllText(path, content);
            logger.LogWarning("Initial admin password also written to {Path} (delete after first sign-in).", path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the initial-admin password file; the password is in the log above.");
        }
    }

    private static string GenerateStrongPassword()
    {
        // 24 chars guaranteeing the configured complexity (upper, lower, digit, symbol).
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+";
        const string all = upper + lower + digits + symbols;

        Span<char> buffer = stackalloc char[24];
        buffer[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        buffer[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        buffer[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        buffer[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < buffer.Length; i++)
            buffer[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        // Fisher-Yates shuffle so the guaranteed chars aren't always in front.
        for (var i = buffer.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }

        return new string(buffer);
    }
}

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

        // Initial host admin
        if (await userManager.Users.AnyAsync(cancellationToken))
            return;

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

        logger.LogWarning(
            "================ INITIAL ADMIN ACCOUNT CREATED ================\n" +
            "  Sign in at the admin UI with:\n" +
            "    Username: {Email}\n" +
            "    Password: {Password}\n" +
            "  CHANGE THIS PASSWORD IMMEDIATELY at /account/change-password.\n" +
            "  This password is shown only once.\n" +
            "==============================================================",
            InitialAdminEmail, password);

        TryWritePasswordFile(password);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

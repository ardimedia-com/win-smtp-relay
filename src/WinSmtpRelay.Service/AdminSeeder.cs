using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Service;

/// <summary>
/// On startup ensures the admin roles and default tenant exist, and brings the administrator accounts into
/// the expected bootstrap state. No administrator is auto-created anymore: the first administrator is
/// defined by the operator through first-run setup (<c>/account/initial-setup</c>). This service only:
/// <list type="bullet">
///   <item>retires the legacy seeded <c>admin@local</c> account when it is the only account, so a real
///   administrator must be defined; and</item>
///   <item>honours the installer's break-glass "reset administrator access" flag by removing all admin
///   accounts (lost-access recovery), again forcing first-run setup.</item>
/// </list>
/// </summary>
public class AdminSeeder(IServiceScopeFactory scopeFactory, ILogger<AdminSeeder> logger) : IHostedService
{
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

        // Break-glass: the installer's "reset administrator access" option sets a registry flag. When set,
        // remove every administrator account (lost-access recovery) so the operator re-defines one via
        // first-run setup. Otherwise, retire the legacy seeded admin@local when it is the only account.
        if (ReadAndClearResetFlag())
            await AdminAccountMaintenance.DeleteAllAdminsAsync(userManager, logger, cancellationToken);
        else
            await AdminAccountMaintenance.DeleteLoneLegacyAdminAsync(
                userManager, AdminBootstrap.InitialAdminEmail, logger, cancellationToken);

        if (!await userManager.Users.AnyAsync(cancellationToken))
            logger.LogWarning(
                "No administrator account exists yet. Open the admin UI — first-run setup at " +
                "/account/initial-setup will prompt you to create the first administrator.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
            logger.LogWarning(ex, "Could not read/clear the admin reset flag.");
        }
        return false;
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WinSmtpRelay.AdminApi.Auth;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Integration.Tests;

/// <summary>
/// Covers <see cref="AdminAccountMaintenance"/>: retiring the legacy seeded <c>admin@local</c> only when it
/// is the sole account, and the break-glass full reset. These guard the new operator-driven bootstrap (no
/// auto-seeded administrator).
/// </summary>
[TestClass]
public class AdminBootstrapTests
{
    private WebApplication _app = null!;
    private string _dbPath = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"adminbootstrap_test_{Guid.NewGuid()}.db");

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRelayStorage($"Data Source={_dbPath}");
        builder.Services.AddRelayAdminAuth();
        _app = builder.Build();

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        await db.Database.MigrateAsync();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AdminRole>>();
        foreach (var role in RelayRoles.All)
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new AdminRole(role));
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _app.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DeleteLoneLegacyAdmin_removes_admin_local_when_it_is_the_only_account()
    {
        using var scope = _app.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        await CreateAdminAsync(userManager, AdminBootstrap.InitialAdminEmail);

        var deleted = await AdminAccountMaintenance.DeleteLoneLegacyAdminAsync(
            userManager, AdminBootstrap.InitialAdminEmail, NullLogger.Instance);

        Assert.IsTrue(deleted);
        Assert.IsFalse(await userManager.Users.AnyAsync());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DeleteLoneLegacyAdmin_keeps_admin_local_when_another_account_exists()
    {
        using var scope = _app.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        await CreateAdminAsync(userManager, AdminBootstrap.InitialAdminEmail);
        await CreateAdminAsync(userManager, "owner@example.com");

        var deleted = await AdminAccountMaintenance.DeleteLoneLegacyAdminAsync(
            userManager, AdminBootstrap.InitialAdminEmail, NullLogger.Instance);

        Assert.IsFalse(deleted);
        Assert.AreEqual(2, await userManager.Users.CountAsync());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DeleteLoneLegacyAdmin_keeps_a_lone_non_legacy_account()
    {
        using var scope = _app.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        await CreateAdminAsync(userManager, "owner@example.com");

        var deleted = await AdminAccountMaintenance.DeleteLoneLegacyAdminAsync(
            userManager, AdminBootstrap.InitialAdminEmail, NullLogger.Instance);

        Assert.IsFalse(deleted);
        Assert.AreEqual(1, await userManager.Users.CountAsync());
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DeleteAllAdmins_removes_every_account()
    {
        using var scope = _app.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        await CreateAdminAsync(userManager, AdminBootstrap.InitialAdminEmail);
        await CreateAdminAsync(userManager, "owner@example.com");

        var removed = await AdminAccountMaintenance.DeleteAllAdminsAsync(userManager, NullLogger.Instance);

        Assert.AreEqual(2, removed);
        Assert.IsFalse(await userManager.Users.AnyAsync());
    }

    private static async Task CreateAdminAsync(UserManager<AdminUser> userManager, string email)
    {
        var user = new AdminUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Test",
            CreatedUtc = DateTimeOffset.UtcNow
        };
        var result = await userManager.CreateAsync(user, "Sup3rSecret!pw12");
        Assert.IsTrue(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}

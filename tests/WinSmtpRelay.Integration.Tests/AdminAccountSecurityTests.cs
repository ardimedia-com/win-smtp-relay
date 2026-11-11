using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.AdminApi.Auth;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Integration.Tests;

/// <summary>
/// Account lifecycle and first-run setup against the real Identity stack and SQLite (M15, M16), and the message-body
/// consent rule for signed-in admins (T1081).
/// </summary>
[TestClass]
public class AdminAccountSecurityTests
{
    private const string Password = "Correct-Horse-9";

    private WebApplication _app = null!;
    private string _dir = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"adminaccount_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRelayStorage($"Data Source={Path.Combine(_dir, "relay.db")}");
        // As the service does in Development: the test account may read the token file (no elevation needed).
        builder.Services.AddSingleton(new InitialSetupToken(grantCurrentUserRead: true));
        builder.Services.AddRelayAdminAuth();
        _app = builder.Build();

        using var scope = _app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Database.MigrateAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AdminRole>>();
        foreach (var role in RelayRoles.All)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new AdminRole(role));
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<AdminUser> CreateUserAsync(string email, string? hostRole = null, (int Tenant, string Role)? tenant = null)
    {
        using var scope = _app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        var user = new AdminUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, Password);
        Assert.IsTrue(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        var memberships = scope.ServiceProvider.GetRequiredService<IAdminMembershipService>();
        if (hostRole is not null)
            await memberships.GrantAsync(user.Id, null, hostRole, null);
        if (tenant is { } t)
            await memberships.GrantAsync(user.Id, t.Tenant, t.Role, null);
        return user;
    }

    private async Task<AdminUser> ReloadAsync(int id)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _app.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    // ---- M15: disable / enable / unlock / reset ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Disable_RotatesTheSecurityStamp_AndIsAudited()
    {
        var actor = await CreateUserAsync("actor@example.com", RelayRoles.HostAdmin);
        var target = await CreateUserAsync("target@example.com", RelayRoles.HostAdmin);
        var stampBefore = (await ReloadAsync(target.Id)).SecurityStamp;

        var result = await InScopeAsync(sp => sp.GetRequiredService<IAdminAccountService>().DisableAsync(target.Id, actor.Id, actor.Email));

        Assert.IsTrue(result.Succeeded, result.Error);
        var after = await ReloadAsync(target.Id);
        Assert.AreNotEqual(stampBefore, after.SecurityStamp, "the open sessions must end");
        Assert.IsTrue(AdminAccountService.IsDisabled(after));
        Assert.AreEqual(1, await InScopeAsync(sp => sp.GetRequiredService<RelayDbContext>().AdminAuditEvents
            .CountAsync(e => e.Action == AdminAuditActions.AdminDisabled && e.TargetUserId == target.Id)));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Disable_RefusesSelf_AndTheLastEnabledHostAdmin()
    {
        var only = await CreateUserAsync("only@example.com", RelayRoles.HostAdmin);
        var tenantUser = await CreateUserAsync("tenant@example.com", tenant: (TenantDefaults.DefaultTenantId, RelayRoles.TenantAdmin));

        var self = await InScopeAsync(sp => sp.GetRequiredService<IAdminAccountService>().DisableAsync(only.Id, only.Id, only.Email));
        Assert.IsFalse(self.Succeeded);
        var last = await InScopeAsync(sp => sp.GetRequiredService<IAdminAccountService>().DisableAsync(only.Id, tenantUser.Id, tenantUser.Email));
        Assert.IsFalse(last.Succeeded);
        StringAssert.Contains(last.Error, "last enabled host admin");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Unlock_ClearsABruteForceLockout_ButNotADisable()
    {
        var actor = await CreateUserAsync("actor@example.com", RelayRoles.HostAdmin);
        var target = await CreateUserAsync("target@example.com", RelayRoles.HostAdmin);
        await LockOutByGuessingAsync(target.Id);
        Assert.IsTrue((await ReloadAsync(target.Id)).LockoutEnd > DateTimeOffset.UtcNow);

        var unlock = await InScopeAsync(sp => sp.GetRequiredService<IAdminAccountService>().UnlockAsync(target.Id, actor.Id, actor.Email));
        Assert.IsTrue(unlock.Succeeded, unlock.Error);
        var after = await ReloadAsync(target.Id);
        Assert.IsNull(after.LockoutEnd);
        Assert.AreEqual(0, after.AccessFailedCount);

        await InScopeAsync(sp => sp.GetRequiredService<IAdminAccountService>().DisableAsync(target.Id, actor.Id, actor.Email));
        var refused = await InScopeAsync(sp => sp.GetRequiredService<IAdminAccountService>().UnlockAsync(target.Id, actor.Id, actor.Email));
        Assert.IsFalse(refused.Succeeded);
        var enable = await InScopeAsync(sp => sp.GetRequiredService<IAdminAccountService>().EnableAsync(target.Id, actor.Id, actor.Email));
        Assert.IsTrue(enable.Succeeded);
        Assert.IsNull((await ReloadAsync(target.Id)).LockoutEnd);
    }

    private async Task LockOutByGuessingAsync(int userId)
    {
        for (var i = 0; i < 5; i++)
        {
            using var scope = _app.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            var user = await users.FindByIdAsync(userId.ToString());
            await users.AccessFailedAsync(user!);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ResetLink_ClearsABruteForceLockout_AndIsAudited()
    {
        var target = await CreateUserAsync("target@example.com", RelayRoles.HostAdmin);
        await LockOutByGuessingAsync(target.Id);

        var token = await InScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<AdminUser>>();
            return await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(target.Id.ToString()))!);
        });
        var result = await InScopeAsync(sp => sp.GetRequiredService<IAdminAccountService>()
            .ResetPasswordWithTokenAsync(target.Id, token, "Brand-New-Pass-1"));

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsNull((await ReloadAsync(target.Id)).LockoutEnd);
        var audit = await InScopeAsync(sp => sp.GetRequiredService<RelayDbContext>().AdminAuditEvents
            .SingleAsync(e => e.Action == AdminAuditActions.AdminPasswordReset));
        StringAssert.Contains(audit.Detail, "lockout cleared");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ResetLink_DoesNotReEnableADisabledAccount()
    {
        var actor = await CreateUserAsync("actor@example.com", RelayRoles.HostAdmin);
        var target = await CreateUserAsync("target@example.com", RelayRoles.HostAdmin);
        await InScopeAsync(sp => sp.GetRequiredService<IAdminAccountService>().DisableAsync(target.Id, actor.Id, actor.Email));

        var token = await InScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<AdminUser>>();
            return await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(target.Id.ToString()))!);
        });
        await InScopeAsync(sp => sp.GetRequiredService<IAdminAccountService>().ResetPasswordWithTokenAsync(target.Id, token, "Brand-New-Pass-1"));

        Assert.IsTrue(AdminAccountService.IsDisabled(await ReloadAsync(target.Id)));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MembershipRevoke_AndRoleChange_RotateTheStamp_SameRoleDoesNot()
    {
        var user = await CreateUserAsync("member@example.com", tenant: (TenantDefaults.DefaultTenantId, RelayRoles.TenantAdmin));
        var s0 = (await ReloadAsync(user.Id)).SecurityStamp;

        await InScopeAsync(sp => sp.GetRequiredService<IAdminMembershipService>().GrantAsync(user.Id, TenantDefaults.DefaultTenantId, RelayRoles.TenantAdmin, null));
        var s1 = (await ReloadAsync(user.Id)).SecurityStamp;
        Assert.AreEqual(s0, s1, "re-granting the same role changes nothing");

        var membership = await InScopeAsync(sp => sp.GetRequiredService<IAdminMembershipService>().GrantAsync(user.Id, TenantDefaults.DefaultTenantId, RelayRoles.TenantViewer, null));
        var s2 = (await ReloadAsync(user.Id)).SecurityStamp;
        Assert.AreNotEqual(s1, s2, "a downgrade must reach the open sessions");

        await InScopeAsync(async sp => { await sp.GetRequiredService<IAdminMembershipService>().RevokeAsync(membership.Id); return 0; });
        Assert.AreNotEqual(s2, (await ReloadAsync(user.Id)).SecurityStamp, "a revoked membership must end the sessions");
    }

    // ---- M16: first-run setup token ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task InitialSetup_NeedsTheToken_WrittenForAdministratorsOnly_AndIsSingleUse()
    {
        var path = await InScopeAsync(sp => sp.GetRequiredService<IInitialAdminSetup>().EnsureTokenIssuedAsync());
        Assert.IsNotNull(path);
        Assert.AreEqual(_dir, Path.GetDirectoryName(path), "the token lives in the data folder (next to the database)");

        var wrong = await InScopeAsync(sp => sp.GetRequiredService<IInitialAdminSetup>()
            .CreateFirstAdminAsync("AAAA-BBBB-CCCC-DDDD-EEEE-FFFF", "first@example.com", null, Password));
        Assert.IsFalse(wrong.Succeeded);
        Assert.IsFalse(wrong.AlreadyConfigured);

        var token = ReadToken(path);
        var ok = await InScopeAsync(sp => sp.GetRequiredService<IInitialAdminSetup>()
            .CreateFirstAdminAsync(token.ToLowerInvariant(), "first@example.com", "First", Password));
        Assert.IsTrue(ok.Succeeded, ok.Error);
        Assert.IsFalse(File.Exists(path), "the token file is removed once used");

        var memberships = await InScopeAsync(sp => sp.GetRequiredService<IAdminMembershipService>().GetForUserAsync(ok.User!.Id));
        Assert.IsTrue(memberships.Any(m => m.TenantId is null && m.Role == RelayRoles.HostAdmin));
        Assert.IsTrue(memberships.Any(m => m.TenantId == TenantDefaults.DefaultTenantId && m.Role == RelayRoles.TenantAdmin));

        var again = await InScopeAsync(sp => sp.GetRequiredService<IInitialAdminSetup>()
            .CreateFirstAdminAsync(token, "second@example.com", null, Password));
        Assert.IsTrue(again.AlreadyConfigured);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task InitialSetup_ConcurrentClaims_CreateExactlyOneAdministrator()
    {
        var path = await InScopeAsync(sp => sp.GetRequiredService<IInitialAdminSetup>().EnsureTokenIssuedAsync());
        var token = ReadToken(path!);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => InScopeAsync(sp =>
            sp.GetRequiredService<IInitialAdminSetup>().CreateFirstAdminAsync(token, $"admin{i}@example.com", null, Password))));

        Assert.AreEqual(1, attempts.Count(a => a.Succeeded));
        Assert.AreEqual(1, await InScopeAsync(sp => sp.GetRequiredService<RelayDbContext>().Users.CountAsync()));
    }

    private static string ReadToken(string path) => File.ReadAllLines(path).Last(l => l.Length > 0).Trim();

    [TestMethod]
    [TestCategory("Integration")]
    public void SetupTokenFile_IsReadableByAdministratorsAndSystemOnly()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var path = new InitialSetupToken().Issue(Path.Combine(_dir, "acl"));

        var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        Assert.IsTrue(rules.All(r => !r.IsInherited), "no inherited access (e.g. Users)");
        var readers = rules.Where(r => (r.FileSystemRights & FileSystemRights.ReadData) != 0)
            .Select(r => r.IdentityReference.Value).ToList();
        CollectionAssert.AreEquivalent(new[]
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        }, readers);
        // The service account may only delete it (to replace it on the next start).
        var self = WindowsIdentity.GetCurrent().User!.Value;
        Assert.IsTrue(rules.Any(r => r.IdentityReference.Value == self && r.FileSystemRights.HasFlag(FileSystemRights.Delete)));
    }

    // ---- T1081: signed-in admins ----

    [TestMethod]
    [TestCategory("Integration")]
    public async Task MessageBody_HostMembershipAlone_IsForbidden_TenantAdminMembershipReads_AndIsAudited()
    {
        var host = await CreateUserAsync("host@example.com", RelayRoles.HostAdmin);
        var viewer = await CreateUserAsync("viewer@example.com", tenant: (TenantDefaults.DefaultTenantId, RelayRoles.TenantViewer));
        var tenantAdmin = await CreateUserAsync("tadmin@example.com", tenant: (TenantDefaults.DefaultTenantId, RelayRoles.TenantAdmin));

        long id;
        using (var scope = _app.Services.CreateScope())
        {
            id = await scope.ServiceProvider.GetRequiredService<IMessageQueue>().EnqueueAsync(new QueuedMessage
            {
                MessageId = "<m@example.com>", Sender = "a@example.com", Recipients = "b@example.com",
                RawMessage = "body"u8.ToArray(), SizeBytes = 4,
            });
        }

        Task<MessageBodyReadResult> ReadAs(AdminUser user) => InScopeAsync(sp =>
        {
            sp.GetRequiredService<ICurrentTenant>().SetHostScope();
            sp.GetRequiredService<ICurrentActor>().Set(user.Id, user.Email);
            return sp.GetRequiredService<IMessageBodyAccess>().ReadAsync(id, "test");
        });

        Assert.AreEqual(MessageBodyReadStatus.Forbidden, (await ReadAs(host)).Status, "host scope is not consent");
        Assert.AreEqual(MessageBodyReadStatus.Forbidden, (await ReadAs(viewer)).Status);
        var read = await ReadAs(tenantAdmin);
        Assert.AreEqual(MessageBodyReadStatus.Ok, read.Status);
        CollectionAssert.AreEqual("body"u8.ToArray(), read.Body);

        var audit = await InScopeAsync(sp => sp.GetRequiredService<RelayDbContext>().AdminAuditEvents
            .SingleAsync(e => e.Action == AdminAuditActions.MessageBodyRead));
        Assert.AreEqual(tenantAdmin.Id, audit.ActorUserId);
        Assert.AreEqual(TenantDefaults.DefaultTenantId, audit.TenantId);
    }
}

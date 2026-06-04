using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class TenantReadinessServiceTests
{
    private RelayDbContext _db = null!;
    private CurrentTenant _current = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _current = new CurrentTenant();
        _current.SetTenant(TenantDefaults.DefaultTenantId);
        _db = new RelayDbContext(options, _current);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    private TenantReadinessService Build(ICurrentTenant current) => new(
        current,
        new TenantService(_db, null!),
        new UserService(_db),
        new AcceptedSenderDomainService(_db),
        new DkimDomainService(_db),
        new SendConnectorService(_db),
        new AcceptedDomainService(_db),
        new IpAccessRuleService(_db),
        new MessageFilterService(_db),
        new ApiKeyService(_db));

    private static SetupItem Item(TenantReadiness r, string key) => r.Items.Single(i => i.Key == key);

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FreshTenant_CannotSend_AndRequiredCredentialsAreTodo()
    {
        var r = await Build(_current).GetAsync();

        Assert.AreEqual(TenantDefaults.DefaultTenantId, r.TenantId);
        Assert.IsTrue(r.TenantActive, "Seeded default tenant is enabled");
        Assert.IsFalse(r.CanSend, "No SMTP users yet, so the tenant cannot send");
        Assert.AreEqual(SetupStatus.Done, Item(r, "tenant-active").Status);
        Assert.AreEqual(SetupStatus.Todo, Item(r, "smtp-users").Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EnabledUser_MakesTenantAbleToSend()
    {
        await new UserService(_db).CreateUserAsync("alice", "P@ssw0rd!");

        var r = await Build(_current).GetAsync();

        Assert.IsTrue(r.CanSend);
        Assert.AreEqual(SetupStatus.Done, Item(r, "smtp-users").Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task DisabledUser_DoesNotCountTowardCanSend()
    {
        await new UserService(_db).CreateUserAsync("bob", "P@ssw0rd!");
        var user = await _db.RelayUsers.FirstAsync(u => u.Username == "bob");
        user.IsEnabled = false;
        await _db.SaveChangesAsync();

        var r = await Build(_current).GetAsync();

        Assert.IsFalse(r.CanSend);
        Assert.AreEqual(SetupStatus.Todo, Item(r, "smtp-users").Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NoSenderDomains_IsPermissive_AndVerificationIsBlocked()
    {
        var r = await Build(_current).GetAsync();

        Assert.AreEqual(SetupStatus.Permissive, Item(r, "sender-domains").Status);
        Assert.AreEqual(SetupStatus.Blocked, Item(r, "sender-verified").Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SenderDomains_PartiallyVerified_ReportsPartial()
    {
        var senders = new AcceptedSenderDomainService(_db);
        var a = await senders.CreateAsync("acme.com");
        await senders.CreateAsync("acme.net");
        await senders.MarkVerifiedAsync(a.Id);

        var r = await Build(_current).GetAsync();

        Assert.AreEqual(SetupStatus.Done, Item(r, "sender-domains").Status);
        var verify = Item(r, "sender-verified");
        Assert.AreEqual(SetupStatus.Partial, verify.Status);
        StringAssert.Contains(verify.Detail, "1 of 2");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SenderDomains_AllVerified_ReportsDone()
    {
        var senders = new AcceptedSenderDomainService(_db);
        var a = await senders.CreateAsync("acme.com");
        await senders.MarkVerifiedAsync(a.Id);

        var r = await Build(_current).GetAsync();

        Assert.AreEqual(SetupStatus.Done, Item(r, "sender-verified").Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EnabledDkim_ReportsDone()
    {
        _db.DkimDomains.Add(new DkimDomain
        {
            Domain = "acme.com",
            Selector = "s1",
            PrivateKeyPath = "C:/keys/acme.pem",
            IsEnabled = true
        });
        await _db.SaveChangesAsync();

        var r = await Build(_current).GetAsync();

        Assert.AreEqual(SetupStatus.Done, Item(r, "dkim").Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalListsEmpty_ArePermissive_AndOutboundIsDoneByDefault()
    {
        var r = await Build(_current).GetAsync();

        Assert.AreEqual(SetupStatus.Done, Item(r, "outbound").Status, "Direct-to-MX is the default");
        Assert.AreEqual(SetupStatus.Permissive, Item(r, "recipient-domains").Status);
        Assert.AreEqual(SetupStatus.Permissive, Item(r, "ip-rules").Status);
        Assert.AreEqual(SetupStatus.Permissive, Item(r, "filters").Status);
        Assert.AreEqual(SetupStatus.Permissive, Item(r, "api-keys").Status);
        Assert.AreEqual(SetupStatus.Permissive, Item(r, "egress-ip").Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RecommendedRollup_CountsOnlyDoneRecommendedItems()
    {
        await new UserService(_db).CreateUserAsync("alice", "P@ssw0rd!");
        var senders = new AcceptedSenderDomainService(_db);
        var a = await senders.CreateAsync("acme.com");
        await senders.MarkVerifiedAsync(a.Id);

        var r = await Build(_current).GetAsync();

        // sender-domains + sender-verified done; dkim still todo => 2 of 3.
        Assert.AreEqual(3, r.RecommendedTotal);
        Assert.AreEqual(2, r.RecommendedDone);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task HostScope_ReturnsEmptyHostScopeResult()
    {
        var host = new CurrentTenant();
        host.SetHostScope();

        var r = await Build(host).GetAsync();

        Assert.IsTrue(r.IsHostScope);
        Assert.IsNull(r.TenantId);
        Assert.IsFalse(r.CanSend);
        Assert.AreEqual(0, r.Items.Count);
    }
}

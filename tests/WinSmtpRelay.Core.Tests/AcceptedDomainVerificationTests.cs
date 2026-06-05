using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class AcceptedDomainVerificationTests
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

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CreateAsync_GeneratesOwnershipToken_AndStartsUnverified()
    {
        var svc = new AcceptedDomainService(_db);

        var created = await svc.CreateAsync("Example.COM");

        Assert.AreEqual("example.com", created.Domain, "domain is normalized to lowercase");
        Assert.AreEqual(32, created.VerificationToken.Length, "token is 16 random bytes as lowercase hex");
        Assert.IsTrue(created.VerificationToken.All(Uri.IsHexDigit));
        Assert.IsNull(created.VerifiedUtc, "a new domain is not yet verified");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task MarkVerifiedAsync_SetsVerifiedTimestamp()
    {
        var svc = new AcceptedDomainService(_db);
        var created = await svc.CreateAsync("acme.test");

        await svc.MarkVerifiedAsync(created.Id);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.AcceptedDomains.FirstAsync(d => d.Id == created.Id);
        Assert.IsNotNull(reloaded.VerifiedUtc);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task MarkVerifiedAsync_UnknownId_IsNoOp()
    {
        var svc = new AcceptedDomainService(_db);
        await svc.MarkVerifiedAsync(99999); // must not throw
        Assert.AreEqual(0, await _db.AcceptedDomains.CountAsync());
    }
}

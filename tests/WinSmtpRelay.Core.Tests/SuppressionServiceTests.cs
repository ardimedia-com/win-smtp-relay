using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class SuppressionServiceTests
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
    public async Task Add_ThenIsSuppressed_TrueAndNormalized()
    {
        var svc = new SuppressionService(_db);
        await svc.AddAsync("User@Example.com", SuppressionReason.HardBounce, "550 mailbox unavailable", TenantDefaults.DefaultTenantId);

        Assert.IsTrue(await svc.IsSuppressedAsync("user@example.com", TenantDefaults.DefaultTenantId));
        // Case/whitespace-insensitive (addresses are normalised to lower-case).
        Assert.IsTrue(await svc.IsSuppressedAsync("  USER@EXAMPLE.COM ", TenantDefaults.DefaultTenantId));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NotSuppressed_ForUnknownAddress()
    {
        var svc = new SuppressionService(_db);
        Assert.IsFalse(await svc.IsSuppressedAsync("nobody@example.com", TenantDefaults.DefaultTenantId));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Add_IsIdempotent()
    {
        var svc = new SuppressionService(_db);
        await svc.AddAsync("a@example.com", SuppressionReason.HardBounce, "first", TenantDefaults.DefaultTenantId);
        await svc.AddAsync("a@example.com", SuppressionReason.Manual, "second", TenantDefaults.DefaultTenantId);

        var all = await svc.GetAllAsync();
        Assert.AreEqual(1, all.Count(e => e.Address == "a@example.com"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Suppression_IsScopedToTenant()
    {
        var svc = new SuppressionService(_db);
        await svc.AddAsync("x@example.com", SuppressionReason.HardBounce, null, TenantDefaults.DefaultTenantId);

        Assert.IsTrue(await svc.IsSuppressedAsync("x@example.com", TenantDefaults.DefaultTenantId));
        Assert.IsFalse(await svc.IsSuppressedAsync("x@example.com", tenantId: 999));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Remove_ReenablesDelivery()
    {
        var svc = new SuppressionService(_db);
        await svc.AddAsync("y@example.com", SuppressionReason.HardBounce, null, TenantDefaults.DefaultTenantId);
        var entry = (await svc.GetAllAsync()).Single();

        await svc.RemoveAsync(entry.Id);

        Assert.IsFalse(await svc.IsSuppressedAsync("y@example.com", TenantDefaults.DefaultTenantId));
    }
}

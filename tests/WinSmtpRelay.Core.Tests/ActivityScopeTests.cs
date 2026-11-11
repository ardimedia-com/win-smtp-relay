using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// <see cref="CurrentTenantExtensions.Owns"/> decides which live-activity events a page shows: SMTP connection events
/// (no tenant, a client IP of any tenant) reach host scope only, like the SignalR hub's host group.
/// </summary>
[TestClass]
public class ActivityScopeTests
{
    private static ActivityEvent Event(ActivityKind kind, int? tenantId) => new(kind, tenantId, DateTimeOffset.UtcNow);

    private static CurrentTenant Host()
    {
        var t = new CurrentTenant();
        t.SetHostScope();
        return t;
    }

    private static CurrentTenant Tenant(int id)
    {
        var t = new CurrentTenant();
        t.SetTenant(id);
        return t;
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void SmtpConnection_IsShownToHostScopeOnly()
    {
        var e = Event(ActivityKind.SmtpConnection, null);
        Assert.IsTrue(Host().Owns(e));
        Assert.IsFalse(Tenant(2).Owns(e));
        Assert.IsFalse(new CurrentTenant().Owns(e), "an unset scope is not a host administrator");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void UntenantedQueueChange_IsShownToEveryone()
    {
        var e = Event(ActivityKind.QueueChanged, null);
        Assert.IsTrue(Host().Owns(e));
        Assert.IsTrue(Tenant(2).Owns(e));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TenantEvent_IsShownToItsTenantAndTheHost()
    {
        var e = Event(ActivityKind.MessageReceived, 2);
        Assert.IsTrue(Host().Owns(e));
        Assert.IsTrue(Tenant(2).Owns(e));
        Assert.IsFalse(Tenant(3).Owns(e));
    }
}

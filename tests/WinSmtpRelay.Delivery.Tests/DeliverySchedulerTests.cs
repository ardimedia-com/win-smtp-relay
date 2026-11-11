using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Delivery.Tests;

/// <summary>M14: tenants take turns, and no destination domain gets more than the cap at once.</summary>
[TestClass]
public class DeliverySchedulerTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void TenantOrder_RotatesAfterTheLastServedTenant()
    {
        var scheduler = new DeliveryScheduler(2);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, scheduler.TenantOrder([3, 1, 2]).ToArray());

        scheduler.Started(10, tenantId: 1, "a@x.test");
        CollectionAssert.AreEqual(new[] { 2, 3, 1 }, scheduler.TenantOrder([1, 2, 3]).ToArray());

        scheduler.Started(11, tenantId: 3, "a@y.test");
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, scheduler.TenantOrder([1, 2, 3]).ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DomainCap_BlocksAThirdDeliveryToTheSameDomain_UntilOneFinishes()
    {
        var scheduler = new DeliveryScheduler(2);
        scheduler.Started(1, 1, "a@slow.test");
        scheduler.Started(2, 1, "b@SLOW.test;c@other.test");

        Assert.IsFalse(scheduler.CanStart(new QueueCandidate(3, 2, "x@slow.test")));
        Assert.IsFalse(scheduler.CanStart(new QueueCandidate(4, 2, "y@fast.test;z@slow.test")), "every domain of a message must have room");
        Assert.IsTrue(scheduler.CanStart(new QueueCandidate(5, 2, "y@fast.test")));

        scheduler.Finished(1);
        Assert.IsTrue(scheduler.CanStart(new QueueCandidate(3, 2, "x@slow.test")));
        CollectionAssert.AreEquivalent(new long[] { 2 }, scheduler.InFlightIds.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InFlightMessage_IsNeverStartedTwice()
    {
        var scheduler = new DeliveryScheduler(5);
        scheduler.Started(7, 1, "a@x.test");
        Assert.IsFalse(scheduler.CanStart(new QueueCandidate(7, 1, "a@x.test")));
    }
}

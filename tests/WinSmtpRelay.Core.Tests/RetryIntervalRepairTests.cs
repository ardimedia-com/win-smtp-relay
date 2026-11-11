using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

/// <summary>
/// The seeder repairs a default send connector whose retry schedule was written while the configuration binder appended
/// the configured list onto the pre-filled default one — and touches nothing else.
/// </summary>
[TestClass]
public class RetryIntervalRepairTests
{
    private static readonly int[] Defaults = [1, 5, 30, 120, 480, 1440];

    [TestMethod]
    [TestCategory("Unit")]
    public void DefaultScheduleDoubled_IsRepaired() =>
        Assert.AreEqual("1,5,30,120,480,1440",
            ConfigurationSeeder.RepairDoubledRetryIntervals("1,5,30,120,480,1440,1,5,30,120,480,1440", Defaults));

    [TestMethod]
    [TestCategory("Unit")]
    public void DefaultFollowedByConfiguredSchedule_IsRepairedToConfigured() =>
        Assert.AreEqual("2,10,60",
            ConfigurationSeeder.RepairDoubledRetryIntervals("1, 5, 30, 120, 480, 1440, 2, 10, 60", new[] { 2, 10, 60 }));

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("1,5,30,120,480,1440")]
    [DataRow("1,5,30,120,480,1440,1,5,30")]
    [DataRow("1,5,30,120,480,1440,1440,1440,1440,1440,1440,1440")]
    [DataRow("5,30,120,480,1440,1,5,30,120,480,1440,1")]
    [DataRow("1,5,30,120,480,1440,1,5,30,120,480,abc")]
    [DataRow("")]
    public void AnyOtherSchedule_IsLeftAlone(string stored) =>
        Assert.IsNull(ConfigurationSeeder.RepairDoubledRetryIntervals(stored, Defaults), stored);

    [TestMethod]
    [TestCategory("Unit")]
    public void DeliberatelyLongSchedule_NotMatchingConfiguration_IsNeverShortened() =>
        Assert.IsNull(ConfigurationSeeder.RepairDoubledRetryIntervals(
            "1,5,30,120,480,1440,1,5,30,120,480,1440", new[] { 2, 10, 60 }));
}

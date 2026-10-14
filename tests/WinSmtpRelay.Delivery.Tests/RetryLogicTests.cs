using Microsoft.Extensions.Configuration;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Delivery;

namespace WinSmtpRelay.Delivery.Tests;

[TestClass]
public class RetryLogicTests
{
    private static DeliveryOptions DefaultOptions => new()
    {
        RetryIntervalsMinutes = [1, 5, 30, 120, 480, 1440],
        MaxRetryHours = 48
    };

    [TestMethod]
    [TestCategory("Unit")]
    public void CalculateNextRetry_FirstRetry_ReturnsInOneMinute()
    {
        var before = DateTime.UtcNow;
        var result = DeliveryWorker.CalculateNextRetry(1, DefaultOptions);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value >= before.AddMinutes(1));
        Assert.IsTrue(result.Value <= DateTime.UtcNow.AddMinutes(1).AddSeconds(5));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CalculateNextRetry_SecondRetry_ReturnsInFiveMinutes()
    {
        var before = DateTime.UtcNow;
        var result = DeliveryWorker.CalculateNextRetry(2, DefaultOptions);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value >= before.AddMinutes(5));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CalculateNextRetry_ZeroRetry_ReturnsNow()
    {
        var before = DateTime.UtcNow;
        var result = DeliveryWorker.CalculateNextRetry(0, DefaultOptions);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value >= before);
        Assert.IsTrue(result.Value <= DateTime.UtcNow.AddSeconds(1));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CalculateNextRetry_ExceedsMaxRetryHours_ReturnsNull()
    {
        // Total of all intervals: 1+5+30+120+480+1440 = 2076 minutes = 34.6 hours
        // After 6 retries, repeating last interval (1440=24h), total > 48h
        var config = new DeliveryOptions
        {
            RetryIntervalsMinutes = [1, 5, 30, 120, 480, 1440],
            MaxRetryHours = 48
        };

        // 7th retry: total = 2076 + 1440 = 3516 min = 58.6h > 48h
        var result = DeliveryWorker.CalculateNextRetry(7, config);
        Assert.IsNull(result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CalculateNextRetry_EmptyIntervals_UsesDefaultSchedule()
    {
        // Empty = not configured = the default schedule (1 min first), never "bounce at once".
        var config = new DeliveryOptions { RetryIntervalsMinutes = [] };
        var before = DateTime.UtcNow;
        var result = DeliveryWorker.CalculateNextRetry(1, config);
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value >= before.AddMinutes(1));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DeliveryOptions_BoundFromConfiguration_KeepsTheConfiguredScheduleOnly()
    {
        // Regression: with defaults in the property initialiser the binder APPENDED appsettings' list to them
        // (12 entries, 69 h > the 48 h window), so every transient failure bounced on its first attempt.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:MaxRetryHours"] = "48",
                ["Delivery:RetryIntervalsMinutes:0"] = "1",
                ["Delivery:RetryIntervalsMinutes:1"] = "5",
                ["Delivery:RetryIntervalsMinutes:2"] = "30",
                ["Delivery:RetryIntervalsMinutes:3"] = "120",
                ["Delivery:RetryIntervalsMinutes:4"] = "480",
                ["Delivery:RetryIntervalsMinutes:5"] = "1440",
            })
            .Build();
        var options = new DeliveryOptions();
        configuration.GetSection(DeliveryOptions.SectionName).Bind(options);

        CollectionAssert.AreEqual(new[] { 1, 5, 30, 120, 480, 1440 }, options.EffectiveRetryIntervalsMinutes);
        Assert.IsNotNull(DeliveryWorker.CalculateNextRetry(1, options), "a first transient failure must be retried");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CalculateNextRetry_BeyondIntervalArray_UsesLastInterval()
    {
        var config = new DeliveryOptions
        {
            RetryIntervalsMinutes = [1, 5],
            MaxRetryHours = 48
        };

        var before = DateTime.UtcNow;
        // 3rd retry: index = min(2, 1) = 1 -> 5 minutes
        var result = DeliveryWorker.CalculateNextRetry(3, config);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value >= before.AddMinutes(5));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CalculateNextRetry_WindowShorterThanIntervalSum_StillRetriesEarlyFailures()
    {
        // Regression: the default intervals sum to 34.6 h; with a 24 h window the old check compared the
        // WHOLE list against the window and bounced every message on its first transient failure (a 421).
        var config = new DeliveryOptions { RetryIntervalsMinutes = [1, 5, 30, 120, 480, 1440], MaxRetryHours = 24 };

        Assert.IsNotNull(DeliveryWorker.CalculateNextRetry(1, config), "1st retry (1 min elapsed) is inside 24 h");
        Assert.IsNotNull(DeliveryWorker.CalculateNextRetry(5, config), "5th retry (636 min elapsed) is inside 24 h");
        Assert.IsNull(DeliveryWorker.CalculateNextRetry(6, config), "6th retry (2076 min) exceeds 24 h");
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Service;

public class StatisticsAggregator(
    IServiceScopeFactory scopeFactory,
    ILogger<StatisticsAggregator> logger) : BackgroundService
{
    // How often to re-check the configured run time while waiting, so an edit to
    // StatisticsRetentionSettings.AggregationTimeUtc is observed without a restart.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Backfill historical data on first run
        try
        {
            logger.LogInformation("Statistics aggregator starting — backfilling historical data");
            using (var scope = scopeFactory.CreateScope())
            {
                var stats = scope.ServiceProvider.GetRequiredService<IStatisticsService>();
                await stats.BackfillAsync(stoppingToken);
            }
            logger.LogInformation("Statistics backfill complete");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Statistics backfill failed");
        }

        // Daily aggregation loop
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait until the next scheduled run, re-reading the configured time periodically so a
            // UI edit to the aggregation time takes effect without a restart (within PollInterval).
            if (!await WaitForNextRunAsync(stoppingToken))
                break;

            try
            {
                var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
                logger.LogInformation("Aggregating statistics for {Date}", yesterday);

                using var scope = scopeFactory.CreateScope();
                var stats = scope.ServiceProvider.GetRequiredService<IStatisticsService>();
                var retentionDays = (await scope.ServiceProvider
                    .GetRequiredService<IStatisticsRetentionSettingsService>().GetAsync(stoppingToken)).RetentionDays;

                await stats.AggregateDayAsync(yesterday, stoppingToken);
                await stats.PurgeOldStatisticsAsync(retentionDays, stoppingToken);

                // Same nightly maintenance pass also applies the data-retention policy (message history,
                // delivery audit log, suppression list). Unscoped scope ⇒ purge spans all tenants.
                await scope.ServiceProvider.GetRequiredService<IRetentionService>().RunPurgeAsync(stoppingToken);

                logger.LogInformation("Statistics aggregation complete for {Date}", yesterday);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Statistics aggregation failed");
            }
        }
    }

    /// <summary>
    /// Sleeps (in capped chunks) until the next configured run time. Re-reads the setting each
    /// chunk so an aggregation-time change is picked up promptly. Returns false if cancelled.
    /// The next run is always measured from the moment the wait STARTED: measured from "now" on every
    /// wake-up it would jump to tomorrow as soon as the target minute is reached, and the nightly
    /// aggregation / retention purge never ran.
    /// </summary>
    private async Task<bool> WaitForNextRunAsync(CancellationToken ct)
    {
        var waitStartedUtc = DateTime.UtcNow;
        var logged = false;
        while (!ct.IsCancellationRequested)
        {
            var settings = await GetRetentionSettingsAsync(ct);
            var wait = DailySchedule.NextRunUtc(settings.AggregationTimeUtc, new TimeOnly(0, 0), waitStartedUtc) - DateTime.UtcNow;
            if (wait <= TimeSpan.Zero)
                return true;

            if (!logged)
            {
                logger.LogInformation("Next statistics aggregation at {Time} UTC (in ~{Delay})", settings.AggregationTimeUtc, wait);
                logged = true;
            }

            try
            {
                await Task.Delay(wait < PollInterval ? wait : PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private async Task<StatisticsRetentionSettings> GetRetentionSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IStatisticsRetentionSettingsService>().GetAsync(ct);
    }
}

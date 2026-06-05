using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Storage;

public class QueueDepthRecorder(
    IServiceScopeFactory scopeFactory,
    ILogger<QueueDepthRecorder> logger) : BackgroundService, IQueueDepthRecorder
{
    private readonly ConcurrentDictionary<string, int> _history = new();

    public int CurrentDepth { get; private set; }

    public IReadOnlyDictionary<string, int> GetHistory()
    {
        Prune();
        return _history;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queue depth recorder starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();
                CurrentDepth = await queue.GetQueueDepthAsync(stoppingToken);

                var key = DateTimeOffset.UtcNow.ToString("HH:mm:ss");
                _history[key] = CurrentDepth;
                Prune();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Queue depth recording error");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private void Prune()
    {
        if (_history.Count <= 90) return;

        var now = DateTimeOffset.UtcNow;
        var validKeys = Enumerable.Range(0, 90)
            .Select(i => now.AddSeconds(-i).ToString("HH:mm:ss"))
            .ToHashSet();

        foreach (var key in _history.Keys)
        {
            if (!validKeys.Contains(key))
                _history.TryRemove(key, out _);
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Service;

/// <summary>
/// Keeps the admin-UI HTTPS certificate current when it was picked from the Windows certificate store: shortly
/// after start and then every six hours it asks <see cref="AdminCertificateApplier.FollowStoreRenewalAsync"/> to
/// switch to a renewed certificate for the same host names (as win-acme / Let's Encrypt writes one every ~60
/// days). The relay serves its own imported copy, so without this it would keep the old certificate until it
/// expires. Best-effort: a failure is logged and retried on the next round; the expiry self-check stays the
/// safety net.
/// </summary>
public sealed class AdminCertificateRenewalService(
    IServiceScopeFactory scopeFactory,
    ILogger<AdminCertificateRenewalService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await CheckOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AdminCertificateApplier>().FollowStoreRenewalAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Checking the certificate store for a renewed admin UI certificate failed");
        }
    }
}

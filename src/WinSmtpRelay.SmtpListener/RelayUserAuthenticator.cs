using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Net;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.SmtpListener;

public class RelayUserAuthenticator : UserAuthenticator, IUserAuthenticator
{
    /// <summary>
    /// Every failed AUTH is answered no sooner than this after it arrived. It levels the timing between an
    /// unknown username (no password hash to check) and a wrong password (one BCrypt verification, about
    /// a quarter of a second at work factor 12), so the response time does not reveal which usernames
    /// exist; and it slows an online guess down for free. Successful logins are not delayed.
    /// </summary>
    internal TimeSpan FailureResponseFloor { get; set; } = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<RelayUserAuthenticator> _logger;

    public RelayUserAuthenticator(
        IServiceScopeFactory scopeFactory,
        RateLimiter rateLimiter,
        ILogger<RelayUserAuthenticator> logger)
    {
        _scopeFactory = scopeFactory;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public override async Task<bool> AuthenticateAsync(
        ISessionContext context,
        string user,
        string password,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var clientIp = context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            ? (ep as IPEndPoint)?.Address.ToString()
            : null;

        // The library resets the session to unauthenticated at every AUTH; so does this relay's own state,
        // or a failed second AUTH would leave the first identity (and its tenant) in place.
        context.Properties.Remove(SessionKeys.AuthenticatedUser);
        context.Properties.Remove(SessionKeys.AuthTenantId);

        // Check if IP is banned before even attempting auth
        if (clientIp is not null && _rateLimiter.IsIpBanned(clientIp))
        {
            _logger.LogWarning("SMTP AUTH rejected from banned IP {Ip}", clientIp);
            await DelayFailureAsync(started, cancellationToken);
            return false;
        }

        // A username under attack from many addresses is locked for the ban period, whoever asks.
        if (_rateLimiter.IsUsernameLocked(user))
        {
            _logger.LogWarning("SMTP AUTH rejected from {Ip}: the username is temporarily locked after repeated failures", clientIp);
            await DelayFailureAsync(started, cancellationToken);
            return false;
        }

        using var scope = _scopeFactory.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        // Resolve the matching user by password so the correct tenant is bound even when the same
        // username exists in multiple tenants (usernames are unique only per tenant).
        var matchedUser = await userService.ValidateAndGetAsync(user, password, cancellationToken);

        if (matchedUser is not null)
        {
            context.Properties[SessionKeys.AuthenticatedUser] = user;
            context.Properties[SessionKeys.AuthTenantId] = matchedUser.TenantId;

            // Deliberately no reset of the failure counters: one valid credential must not let its holder
            // wipe the record of guesses made from the same address. Failures age out of the window instead.
            _logger.LogInformation("SMTP AUTH successful for user {User}", user);
            return true;
        }

        _logger.LogWarning("SMTP AUTH failed for user {User} from {Ip}", user, clientIp);
        await _rateLimiter.RecordFailedAuthAsync(clientIp, user, cancellationToken);
        await DelayFailureAsync(started, cancellationToken);
        return false;
    }

    private async Task DelayFailureAsync(long started, CancellationToken cancellationToken)
    {
        var remaining = FailureResponseFloor - Stopwatch.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, cancellationToken);
    }
}

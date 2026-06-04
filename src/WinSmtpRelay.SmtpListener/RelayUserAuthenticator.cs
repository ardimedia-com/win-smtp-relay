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
        var clientIp = context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            ? (ep as IPEndPoint)?.Address.ToString()
            : null;

        // Check if IP is banned before even attempting auth
        if (clientIp is not null && _rateLimiter.IsIpBanned(clientIp))
        {
            _logger.LogWarning("SMTP AUTH rejected for {User} from banned IP {Ip}", user, clientIp);
            return false;
        }

        using var scope = _scopeFactory.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        // Resolve the matching user by password so the correct tenant is bound even when the same
        // username exists in multiple tenants (usernames are unique only per tenant).
        var matchedUser = await userService.ValidateAndGetAsync(user, password, cancellationToken);
        var result = matchedUser is not null;

        if (result)
        {
            context.Properties["AuthenticatedUser"] = user;
            context.Properties["TenantId"] = matchedUser!.TenantId;

            _logger.LogInformation("SMTP AUTH successful for user {User}", user);

            // Clear failed auth counter on success
            if (clientIp is not null)
                _rateLimiter.ClearFailedAuth(clientIp);
        }
        else
        {
            _logger.LogWarning("SMTP AUTH failed for user {User}", user);

            // Track failed auth for auto-ban
            if (clientIp is not null)
                await _rateLimiter.RecordFailedAuthAsync(clientIp, cancellationToken);
        }

        return result;
    }
}

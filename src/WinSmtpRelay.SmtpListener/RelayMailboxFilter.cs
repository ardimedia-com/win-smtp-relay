using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Storage;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.SmtpListener;

public class RelayMailboxFilter : MailboxFilter, IMailboxFilter
{
    private readonly SmtpListenerOptions _options;
    private readonly BackupMxOptions _backupMxOptions;
    private readonly EmailAuthenticationService _emailAuth;
    private readonly RateLimiter _rateLimiter;
    private readonly IRuntimeConfigCache _configCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RelayMailboxFilter> _logger;

    public RelayMailboxFilter(
        IOptions<SmtpListenerOptions> options,
        IOptions<BackupMxOptions> backupMxOptions,
        EmailAuthenticationService emailAuth,
        RateLimiter rateLimiter,
        IRuntimeConfigCache configCache,
        IServiceScopeFactory scopeFactory,
        ILogger<RelayMailboxFilter> logger)
    {
        _options = options.Value;
        _backupMxOptions = backupMxOptions.Value;
        _emailAuth = emailAuth;
        _rateLimiter = rateLimiter;
        _configCache = configCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public override async Task<bool> CanAcceptFromAsync(
        ISessionContext context,
        IMailbox from,
        int size,
        CancellationToken cancellationToken)
    {
        // Check message size limit
        if (size > 0 && size > _options.MaxMessageSizeBytes)
        {
            _logger.LogWarning("Message from {Sender} rejected: size {Size} exceeds limit {Limit}",
                from.AsAddress(), size, _options.MaxMessageSizeBytes);
            return false;
        }

        var remoteEndPoint = context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep) ? ep as IPEndPoint : null;
        var clientIp = remoteEndPoint?.Address.ToString();

        // Resolve the owning tenant for this message. Authenticated sessions set it at AUTH time;
        // otherwise resolve by sender domain (falls back to the default tenant). Stamped onto the
        // queued message in RelayMessageStore.
        if (!context.Properties.ContainsKey("TenantId"))
        {
            var ownerTenant = await _configCache.GetTenantForSenderDomainAsync(
                GetDomainFromAddress(from.AsAddress()), cancellationToken);
            context.Properties["TenantId"] = ownerTenant ?? Core.Models.TenantDefaults.DefaultTenantId;
        }

        // Reject mail for a disabled tenant (the owning tenant was just resolved above).
        var resolvedTenantId = context.Properties.TryGetValue("TenantId", out var tid) && tid is int tenantId
            ? tenantId
            : Core.Models.TenantDefaults.DefaultTenantId;
        if (!await _configCache.IsTenantEnabledAsync(resolvedTenantId, cancellationToken))
        {
            _logger.LogWarning("Message from {Sender} rejected: tenant {TenantId} is disabled",
                from.AsAddress(), resolvedTenantId);
            return false;
        }

        // Check if IP is auto-banned (failed auth)
        if (clientIp is not null && _rateLimiter.IsIpBanned(clientIp))
        {
            _logger.LogWarning("Connection from {ClientIp} rejected: IP is auto-banned", clientIp);
            return false;
        }

        // Check per-IP rate limit
        if (clientIp is not null && !_rateLimiter.IsIpAllowed(clientIp))
        {
            return false;
        }

        // Check IP-based relay restrictions. DB-stored IP access rules are authoritative
        // (Allow/Deny, first match by sort order); fall back to the static appsettings
        // AllowedNetworks list only when no DB rules exist.
        if (remoteEndPoint is not null)
        {
            var ipRules = await _configCache.GetIpAccessRulesAsync(cancellationToken);
            // Evaluate only this tenant's rules plus the host baseline — one tenant's rules
            // must never allow or deny another tenant's traffic on the shared listener.
            var decision = IpAccessEvaluator.EvaluateForTenant(remoteEndPoint.Address, ipRules, resolvedTenantId);

            if (decision is false)
            {
                _logger.LogWarning("Relay denied for {ClientIp}: blocked by IP access rules", remoteEndPoint.Address);
                return false;
            }

            if (decision is null &&
                _options.AllowedNetworks.Count > 0 &&
                !IpNetworkHelper.IsInAnyNetwork(remoteEndPoint.Address, _options.AllowedNetworks))
            {
                _logger.LogWarning("Relay denied for {ClientIp}: not in allowed networks", remoteEndPoint.Address);
                return false;
            }
        }

        // Check per-sender rate limit
        if (!_rateLimiter.IsSenderAllowed(from.AsAddress()))
        {
            return false;
        }

        // Check accepted sender domains (from DB cache)
        var senderDomainForCheck = GetDomainFromAddress(from.AsAddress());
        var acceptedSenderDomains = await _configCache.GetAcceptedSenderDomainsAsync(cancellationToken);
        if (acceptedSenderDomains.Count > 0)
        {
            var senderDomainAccepted = acceptedSenderDomains.Any(d =>
                string.Equals(d, senderDomainForCheck, StringComparison.OrdinalIgnoreCase));

            if (!senderDomainAccepted)
            {
                _logger.LogWarning("Sender {Sender} rejected: domain {Domain} not in accepted sender domains",
                    from.AsAddress(), senderDomainForCheck);
                return false;
            }
        }

        // Per-user SendAs enforcement
        var authenticatedUser = GetAuthenticatedUser(context);
        if (authenticatedUser is not null)
        {
            var senderAddress = from.AsAddress();

            // Check SendAs
            if (!await IsAllowedSenderAsync(authenticatedUser, senderAddress, cancellationToken))
            {
                _logger.LogWarning("User {User} not allowed to send as {Sender}", authenticatedUser, senderAddress);
                return false;
            }

            // Check rate limit
            var user = await GetUserAsync(authenticatedUser, cancellationToken);
            if (user is not null && !_rateLimiter.IsAllowed(authenticatedUser, user.RateLimitPerMinute, user.RateLimitPerDay))
            {
                _logger.LogWarning("Rate limit exceeded for user {User}", authenticatedUser);
                return false;
            }
        }

        // SPF check (store result in context for later use in SaveAsync)
        if (remoteEndPoint is not null)
        {
            var senderDomain = GetDomainFromAddress(from.AsAddress());
            var spfResult = await _emailAuth.CheckSpfAsync(remoteEndPoint.Address, senderDomain, cancellationToken);
            context.Properties["SpfResult"] = spfResult;
            context.Properties["EnvelopeFromDomain"] = senderDomain;

            // In Reject mode, reject on SPF hard fail
            if (spfResult.Verdict == Security.Models.SpfVerdict.Fail &&
                _emailAuth.ShouldReject(new Security.Models.AuthenticationResults(
                    spfResult, new Security.Models.DmarcCheckResult(
                        Security.Models.DmarcVerdict.None, Security.Models.DmarcPolicy.None, ""))))
            {
                _logger.LogWarning("Message from {Sender} rejected: SPF fail from {Ip}",
                    from.AsAddress(), remoteEndPoint.Address);
                return false;
            }
        }

        return true;
    }

    public override async Task<bool> CanDeliverToAsync(
        ISessionContext context,
        IMailbox to,
        IMailbox from,
        CancellationToken cancellationToken)
    {
        var recipientDomain = to.Host;

        // Always accept mail for backup MX domains
        if (_backupMxOptions.Enabled &&
            _backupMxOptions.Domains.Any(d => string.Equals(d, recipientDomain, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // If accepted domains are configured, check recipient domain (from DB cache)
        var acceptedDomains = await _configCache.GetAcceptedDomainsAsync(cancellationToken);
        if (acceptedDomains.Count > 0)
        {
            var accepted = acceptedDomains.Any(d =>
                string.Equals(d, recipientDomain, StringComparison.OrdinalIgnoreCase));

            if (!accepted)
            {
                _logger.LogWarning("Recipient {Recipient} rejected: domain {Domain} not in accepted domains",
                    to.AsAddress(), recipientDomain);
                return false;
            }
        }

        return true;
    }

    private static string? GetAuthenticatedUser(ISessionContext context)
    {
        return context.Properties.TryGetValue("AuthenticatedUser", out var user)
            ? user as string
            : null;
    }

    private async Task<bool> IsAllowedSenderAsync(string username, string senderAddress, CancellationToken cancellationToken)
    {
        var user = await GetUserAsync(username, cancellationToken);
        if (user?.AllowedSenderAddresses is null or "")
            return true; // no restriction

        var allowed = user.AllowedSenderAddresses.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Any(a => string.Equals(a, senderAddress, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Core.Models.RelayUser?> GetUserAsync(string username, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        return await userService.GetByUsernameAsync(username, cancellationToken);
    }

    private static string GetDomainFromAddress(string emailAddress)
    {
        var atIndex = emailAddress.LastIndexOf('@');
        return atIndex >= 0 ? emailAddress[(atIndex + 1)..] : emailAddress;
    }
}

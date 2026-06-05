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
    private readonly EmailAuthenticationService _emailAuth;
    private readonly RateLimiter _rateLimiter;
    private readonly IRuntimeConfigCache _configCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RelayMailboxFilter> _logger;

    public RelayMailboxFilter(
        IOptions<SmtpListenerOptions> options,
        EmailAuthenticationService emailAuth,
        RateLimiter rateLimiter,
        IRuntimeConfigCache configCache,
        IServiceScopeFactory scopeFactory,
        ILogger<RelayMailboxFilter> logger)
    {
        _options = options.Value;
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

        // IP access rules are needed both for strict tenant binding (just below) and the relay access
        // check further down — load once (cached).
        var ipRules = await _configCache.GetIpAccessRulesAsync(cancellationToken);

        // Resolve the owning tenant for this message. Authenticated sessions set it at AUTH time;
        // otherwise attribute it here — by sender domain, or, under strict mode, by the matching
        // tenant Allow IP rule (with anti-spoofing / no-silent-default). Stamped onto the queued
        // message in RelayMessageStore.
        if (!context.Properties.ContainsKey("TenantId"))
        {
            var emailAuth = await _configCache.GetEmailAuthSettingsAsync(cancellationToken);
            var domainTenant = await _configCache.GetTenantForSenderDomainAsync(
                GetDomainFromAddress(from.AsAddress()), cancellationToken);

            int? ipTenant = null;
            var ipAmbiguous = false;
            if (emailAuth.BindTenantToAllowIpRule && remoteEndPoint is not null)
                (ipTenant, ipAmbiguous) = UnauthenticatedTenantResolver.TenantFromAllowRules(remoteEndPoint.Address, ipRules);

            var (tenant, outcome) = UnauthenticatedTenantResolver.Decide(
                domainTenant, ipTenant, ipAmbiguous,
                emailAuth.BindTenantToAllowIpRule, emailAuth.RejectUnresolvedTenant);

            if (outcome != UnauthenticatedTenantResolver.Outcome.Resolved)
            {
                _logger.LogWarning("Message from {Sender} ({ClientIp}) rejected: unauthenticated tenant attribution failed ({Outcome})",
                    from.AsAddress(), clientIp, outcome);
                return false;
            }

            context.Properties["TenantId"] = tenant!.Value;
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
        if (clientIp is not null && !await _rateLimiter.IsIpAllowedAsync(clientIp, cancellationToken))
        {
            return false;
        }

        // Check IP-based relay restrictions. DB-stored IP access rules are authoritative
        // (Allow/Deny, first match by sort order); fall back to the static appsettings
        // AllowedNetworks list only when no DB rules exist.
        if (remoteEndPoint is not null)
        {
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
        if (!await _rateLimiter.IsSenderAllowedAsync(from.AsAddress(), cancellationToken))
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

            // Optional verification gate: when enabled, an accepted sender domain must also have its
            // ownership verified (DNS TXT) before it may send. Applies only among configured domains.
            var emailAuth = await _configCache.GetEmailAuthSettingsAsync(cancellationToken);
            if (emailAuth.RequireSenderDomainVerification)
            {
                var verified = await _configCache.GetVerifiedSenderDomainsAsync(cancellationToken);
                if (!verified.Contains(senderDomainForCheck))
                {
                    _logger.LogWarning("Sender {Sender} rejected: domain {Domain} ownership not verified (verification required)",
                        from.AsAddress(), senderDomainForCheck);
                    return false;
                }
            }
        }

        // Per-user SendAs enforcement
        var authenticatedUser = GetAuthenticatedUser(context);
        if (authenticatedUser is not null)
        {
            var senderAddress = from.AsAddress();

            // Resolve the user within the authenticated session's tenant — usernames are unique only
            // per tenant, so a username-only lookup could load another tenant's user (wrong SendAs /
            // rate limits). The tenant was bound at AUTH (RelayUserAuthenticator) into "TenantId".
            var authTenantId = context.Properties.TryGetValue("TenantId", out var atid) && atid is int t
                ? t
                : Core.Models.TenantDefaults.DefaultTenantId;
            var user = await GetUserAsync(authenticatedUser, authTenantId, cancellationToken);

            // Check SendAs
            if (!IsAllowedSender(user, senderAddress))
            {
                _logger.LogWarning("User {User} not allowed to send as {Sender}", authenticatedUser, senderAddress);
                return false;
            }

            // Check rate limit — keyed by the globally-unique user id so two tenants sharing a
            // username don't share a bucket.
            if (user is not null && !_rateLimiter.IsAllowed($"user:{user.Id}", user.RateLimitPerMinute, user.RateLimitPerDay))
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
            var spfOnlyResults = new Security.Models.AuthenticationResults(
                spfResult, new Security.Models.DmarcCheckResult(
                    Security.Models.DmarcVerdict.None, Security.Models.DmarcPolicy.None, ""));
            if (spfResult.Verdict == Security.Models.SpfVerdict.Fail &&
                await _emailAuth.ShouldRejectAsync(spfOnlyResults, cancellationToken))
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

        // Always accept mail for backup MX domains (read live from the runtime config cache)
        var backupMx = await _configCache.GetBackupMxSettingsAsync(cancellationToken);
        if (backupMx.Enabled &&
            backupMx.DomainList.Any(d => string.Equals(d, recipientDomain, StringComparison.OrdinalIgnoreCase)))
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

            // Optional verification gate: when enabled, an accepted recipient domain must also have its
            // ownership verified (DNS TXT) before the relay accepts mail for it. Applies only among
            // configured domains; backup-MX domains were already accepted above.
            var emailAuth = await _configCache.GetEmailAuthSettingsAsync(cancellationToken);
            if (emailAuth.RequireRecipientDomainVerification)
            {
                var verified = await _configCache.GetVerifiedRecipientDomainsAsync(cancellationToken);
                if (!verified.Contains(recipientDomain))
                {
                    _logger.LogWarning("Recipient {Recipient} rejected: domain {Domain} ownership not verified (verification required)",
                        to.AsAddress(), recipientDomain);
                    return false;
                }
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

    private static bool IsAllowedSender(Core.Models.RelayUser? user, string senderAddress)
    {
        if (user?.AllowedSenderAddresses is null or "")
            return true; // no restriction

        var allowed = user.AllowedSenderAddresses.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Any(a => string.Equals(a, senderAddress, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Core.Models.RelayUser?> GetUserAsync(string username, int tenantId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        return await userService.GetByUsernameAsync(username, tenantId, cancellationToken);
    }

    private static string GetDomainFromAddress(string emailAddress)
    {
        var atIndex = emailAddress.LastIndexOf('@');
        return atIndex >= 0 ? emailAddress[(atIndex + 1)..] : emailAddress;
    }
}

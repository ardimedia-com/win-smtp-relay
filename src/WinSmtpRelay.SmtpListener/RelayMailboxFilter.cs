using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.SmtpListener;

public class RelayMailboxFilter : MailboxFilter, IMailboxFilter
{
    private readonly SmtpListenerOptions _options;
    private readonly RateLimiter _rateLimiter;
    private readonly IRuntimeConfigCache _configCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRejectionRecorder _rejectionRecorder;
    private readonly ILogger<RelayMailboxFilter> _logger;

    /// <summary>
    /// The reply code a plain <c>return false</c> from this class produces. <see cref="IMailboxFilter"/>
    /// returns a bool, and the library maps <c>false</c> onto the fixed
    /// <c>SmtpResponse.MailboxUnavailable</c> (550) — see MailCommand/RcptCommand in SmtpServer v11.1.0.
    /// The gate's reason cannot travel over the wire, which is precisely why it is recorded here instead.
    /// The permanent gates deliberately stay on this uniform 550: a distinct text per gate would tell
    /// every scanner which policy refused it (owner decision 2026-07-16).
    /// </summary>
    private const int MailboxFilterReplyCode = 550;

    /// <summary>
    /// Marks a thrown rejection as already recorded by this filter, so the ResponseException hook in
    /// SmtpRelayServer (which fires for every thrown SmtpResponseException) does not record it a second
    /// time under a protocol-level reason.
    /// </summary>
    public const string RecordedPropertyKey = "WinSmtpRelay:RejectionRecorded";

    /// <summary>
    /// A rejection that is expected to clear on its own must NOT be a permanent 550: a correct sending
    /// MTA treats 550 as final and bounces the message, so throttling a legitimate sender would lose its
    /// mail instead of delaying it. 451 tells the client to queue and retry later.
    /// </summary>
    private static readonly SmtpResponse ThrottledResponse =
        new(SmtpReplyCode.Aborted, "rate limit exceeded, try again later");

    /// <summary>
    /// An auto-banned IP gets 421 and the session is CLOSED — a banned client must not sit on an open
    /// session probing further, and 421 (unlike 550) does not tell it anything beyond "go away for now".
    /// The ban itself expires after the configured cooldown.
    /// </summary>
    private static readonly SmtpResponse BannedResponse =
        new(SmtpReplyCode.ServiceUnavailable, "service not available, closing transmission channel");

    /// <summary>RFC 3207 §4: an endpoint that requires TLS refuses mail commands before STARTTLS with 530.</summary>
    internal static readonly SmtpResponse StartTlsRequiredResponse =
        new(SmtpReplyCode.AuthenticationRequired, "5.7.0 Must issue a STARTTLS command first");

    /// <summary>
    /// RFC 5321 §4.5.3.1.10: a server that limits recipients answers the excess RCPT with 452, which the
    /// client treats as "send the rest in another transaction" — nothing is lost.
    /// </summary>
    internal static readonly SmtpResponse TooManyRecipientsResponse =
        new(SmtpReplyCode.InsufficientStorage, "4.5.3 Too many recipients");

    public RelayMailboxFilter(
        IOptions<SmtpListenerOptions> options,
        RateLimiter rateLimiter,
        IRuntimeConfigCache configCache,
        IServiceScopeFactory scopeFactory,
        IRejectionRecorder rejectionRecorder,
        ILogger<RelayMailboxFilter> logger)
    {
        _options = options.Value;
        _rateLimiter = rateLimiter;
        _configCache = configCache;
        _scopeFactory = scopeFactory;
        _rejectionRecorder = rejectionRecorder;
        _logger = logger;
    }

    /// <summary>
    /// Refuses the submission AND records why, so every gate stays a one-line <c>return Reject(...)</c>.
    /// Reads the client IP and tenant from the session rather than taking them as parameters, so a gate
    /// that runs before those locals exist still produces a complete row. Returns false so callers can
    /// <c>return Reject(...)</c> directly.
    /// </summary>
    private bool Reject(ISessionContext context, RejectReason reason, string? senderDomain = null, string? detail = null)
    {
        Record(context, reason, MailboxFilterReplyCode, senderDomain, detail);
        return false;
    }

    /// <summary>
    /// Refuses the submission with a SPECIFIC reply instead of the library's fixed 550, by throwing the
    /// library's own <see cref="SmtpResponseException"/>. A throw from CanAcceptFromAsync/CanDeliverToAsync
    /// propagates out of MailCommand/RcptCommand into the session loop's generic catch, which writes THIS
    /// response to the client — the same mechanism every library-internal error reply uses; no fork, no
    /// custom command. Verified against SmtpServer v11.1.0 (the reference floats: re-verify on a bump).
    /// The marker property stops the ResponseException hook from recording the throw a second time.
    /// </summary>
    private void RejectWith(ISessionContext context, RejectReason reason, SmtpResponse response, bool quit,
        string? senderDomain = null, string? detail = null)
    {
        Record(context, reason, (int)response.ReplyCode, senderDomain, detail);
        throw new SmtpResponseException(response, quit,
            new Dictionary<string, object> { [RecordedPropertyKey] = true });
    }

    private void Record(ISessionContext context, RejectReason reason, int replyCode, string? senderDomain, string? detail)
    {
        var clientIp = (context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            ? ep as IPEndPoint
            : null)?.Address.ToString();

        // Null until the attribution block below runs — and null for good on the very gate that reports
        // attribution failing. Such a row is host-level by design; there is no tenant to own it.
        var tenantId = context.Properties.TryGetValue(SessionKeys.TenantId, out var tid) && tid is int t ? t : (int?)null;

        _rejectionRecorder.Record(clientIp, reason, replyCode, senderDomain, tenantId, detail);
    }

    public override async Task<bool> CanAcceptFromAsync(
        ISessionContext context,
        IMailbox from,
        int size,
        CancellationToken cancellationToken)
    {
        // A new MAIL starts a new message: nothing the previous transaction decided may carry over — above
        // all not its tenant (a second MAIL after RSET used to run under the first message's tenant).
        foreach (var key in SessionKeys.MessageScoped)
            context.Properties.Remove(key);

        // Resolve the client up front: every gate below reports it, including the size check, which used
        // to run before the endpoint was read and so could only ever log a nameless rejection.
        var remoteEndPoint = context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep) ? ep as IPEndPoint : null;
        var clientIp = remoteEndPoint?.Address.ToString();

        // MAIL FROM:<> is the null sender of bounces and auto-replies. The library hands it over as an
        // empty mailbox whose address is null; it is "" everywhere here and has no domain.
        var sender = SenderAddress(from);
        var isNullSender = sender.Length == 0;
        var senderDomain = GetDomainFromAddress(sender);

        // Check message size limit
        if (size > 0 && size > _options.MaxMessageSizeBytes)
        {
            _logger.LogWarning("Message from {Sender} ({ClientIp}) rejected: size {Size} exceeds limit {Limit}",
                sender, clientIp, size, _options.MaxMessageSizeBytes);
            return Reject(context, RejectReason.MessageTooLarge, senderDomain,
                $"{size} bytes exceeds the {_options.MaxMessageSizeBytes}-byte limit");
        }

        // An endpoint that requires TLS must refuse the transaction until STARTTLS succeeded — offering
        // STARTTLS alone lets a client (or a downgrade attacker) carry on in plain text.
        if (context.Properties.ContainsKey(SessionKeys.RequireTls) && context.Pipe?.IsSecure != true)
        {
            _logger.LogWarning("Message from {Sender} ({ClientIp}) rejected: the endpoint requires STARTTLS first", sender, clientIp);
            RejectWith(context, RejectReason.TlsRequired, StartTlsRequiredResponse, quit: false, senderDomain);
        }

        // IP access rules are needed both for strict tenant binding (just below) and the relay access
        // check further down — load once (cached).
        var ipRules = await _configCache.GetIpAccessRulesAsync(cancellationToken);

        // Resolve the owning tenant for THIS message. An authenticated session's tenant was bound at AUTH
        // and is kept under its own key, so it cannot be lost or replaced here; an unauthenticated message
        // is attributed afresh at every MAIL — by sender domain, or, under strict mode, by the matching
        // tenant Allow IP rule (with anti-spoofing / no-silent-default). Stamped onto the queued message
        // in RelayMessageStore.
        var authenticatedUser = GetAuthenticatedUser(context);
        int resolvedTenantId;
        if (authenticatedUser is not null)
        {
            resolvedTenantId = context.Properties.TryGetValue(SessionKeys.AuthTenantId, out var atid) && atid is int authTenant
                ? authTenant
                : TenantDefaults.DefaultTenantId;
        }
        else
        {
            var emailAuth = await _configCache.GetEmailAuthSettingsAsync(cancellationToken);

            // A null sender has no domain to claim a tenant with: it is attributed like an unclaimed domain.
            var domainTenant = isNullSender
                ? null
                : await _configCache.GetTenantForSenderDomainAsync(senderDomain, cancellationToken);

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
                    sender, clientIp, outcome);
                return Reject(context, RejectReason.TenantAttributionFailed, senderDomain, outcome.ToString());
            }

            resolvedTenantId = tenant!.Value;
        }

        context.Properties[SessionKeys.TenantId] = resolvedTenantId;

        // Reject mail for a disabled tenant (the owning tenant was just resolved above).
        if (!await _configCache.IsTenantEnabledAsync(resolvedTenantId, cancellationToken))
        {
            _logger.LogWarning("Message from {Sender} rejected: tenant {TenantId} is disabled",
                sender, resolvedTenantId);
            return Reject(context, RejectReason.TenantDisabled, senderDomain);
        }

        // Check if IP is auto-banned (failed auth). 421 + session close: a banned client must not keep
        // an open session to probe from, and the ban expires on its own.
        if (clientIp is not null && _rateLimiter.IsIpBanned(clientIp))
        {
            _logger.LogWarning("Connection from {ClientIp} rejected: IP is auto-banned", clientIp);
            RejectWith(context, RejectReason.IpAutoBanned, BannedResponse, quit: true, senderDomain);
        }

        // Check per-IP rate limit (RateLimiter logs the breach itself; recorded here so it is queryable).
        // 451, not 550: throttling is temporary, and a permanent answer would make the sender bounce the
        // mail instead of retrying it.
        if (clientIp is not null && !await _rateLimiter.IsIpAllowedAsync(clientIp, cancellationToken))
        {
            RejectWith(context, RejectReason.IpRateLimitExceeded, ThrottledResponse, quit: false, senderDomain);
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
                return Reject(context, RejectReason.IpBlocked, senderDomain);
            }

            if (decision is null &&
                _options.AllowedNetworks.Count > 0 &&
                !IpNetworkHelper.IsInAnyNetwork(remoteEndPoint.Address, _options.AllowedNetworks))
            {
                _logger.LogWarning("Relay denied for {ClientIp}: not in allowed networks", remoteEndPoint.Address);
                return Reject(context, RejectReason.NotInAllowedNetworks, senderDomain);
            }
        }

        // Per-sender budget (recipients, recorded at RCPT). The null sender has no budget of its own: every
        // bounce would share one bucket, and it may only address hosted domains anyway (CanDeliverToAsync).
        if (!isNullSender && !await _rateLimiter.IsSenderWithinLimitAsync(sender, cancellationToken))
        {
            RejectWith(context, RejectReason.SenderRateLimitExceeded, ThrottledResponse, quit: false, senderDomain);
        }

        // Accepted sender domains. The same gate runs on the header From in RelayMessageStore, because the
        // header From is what DKIM signs and what the recipient sees. A null sender has no domain to check;
        // it is limited to hosted recipients instead.
        if (!isNullSender)
        {
            var domainGate = await CheckSenderDomainAsync(senderDomain, cancellationToken);
            if (domainGate is not null)
            {
                _logger.LogWarning("Sender {Sender} rejected: domain {Domain} {Gate}", sender, senderDomain,
                    domainGate == RejectReason.SenderDomainNotAccepted ? "not in accepted sender domains" : "ownership not verified (verification required)");
                return Reject(context, domainGate.Value, senderDomain);
            }
        }

        // Per-user SendAs enforcement
        if (authenticatedUser is not null)
        {
            // Resolve the user within the authenticated session's tenant — usernames are unique only
            // per tenant, so a username-only lookup could load another tenant's user (wrong SendAs /
            // rate limits).
            var user = await GetUserAsync(authenticatedUser, resolvedTenantId, cancellationToken);
            if (user is not null)
                context.Properties[SessionKeys.RelayUser] = user;

            // Check SendAs
            if (!IsAllowedSender(user, sender))
            {
                _logger.LogWarning("User {User} not allowed to send as {Sender}", authenticatedUser, sender.Length == 0 ? "<>" : sender);
                return Reject(context, RejectReason.SendAsDenied, senderDomain, $"user {authenticatedUser}");
            }

            // Per-user budget — keyed by the globally-unique user id so two tenants sharing a username don't
            // share a bucket. Checked here, consumed per accepted recipient.
            if (user is not null && !_rateLimiter.IsUserWithinLimit(UserRateKey(user), user.RateLimitPerMinute, user.RateLimitPerDay))
            {
                _logger.LogWarning("Rate limit exceeded for user {User}", authenticatedUser);
                RejectWith(context, RejectReason.UserRateLimitExceeded, ThrottledResponse, quit: false,
                    senderDomain, $"user {authenticatedUser}");
            }
        }

        // SPF/DMARC are evaluated once, at the end of DATA, where the header From is known: rejecting on
        // SPF alone here would ignore the From domain's DMARC policy (a DKIM-aligned pass overrides an SPF
        // fail — the forwarded-mail case). See RelayMessageStore.
        return true;
    }

    public override async Task<bool> CanDeliverToAsync(
        ISessionContext context,
        IMailbox to,
        IMailbox from,
        CancellationToken cancellationToken)
    {
        var recipientDomain = to.Host;
        // The envelope sender's domain identifies WHO was refused; the recipient is carried in Detail.
        // (Resolved up front so the verification gate below can report a complete row — it used to run
        // before the endpoint was read.)
        var sender = SenderAddress(from);
        var isNullSender = sender.Length == 0;
        var senderDomain = GetDomainFromAddress(sender);

        // Recipient cap per message: 452 lets the client deliver the rest in a further transaction.
        var accepted = context.Properties.TryGetValue(SessionKeys.RecipientCount, out var rc) && rc is int n ? n : 0;
        if (_options.MaxRecipientsPerMessage > 0 && accepted >= _options.MaxRecipientsPerMessage)
        {
            _logger.LogWarning("Recipient {Recipient} deferred: the message already has {Count} recipients (limit {Limit})",
                to.AsAddress(), accepted, _options.MaxRecipientsPerMessage);
            RejectWith(context, RejectReason.TooManyRecipients, TooManyRecipientsResponse, quit: false, senderDomain,
                $"limit {_options.MaxRecipientsPerMessage}");
        }

        var decision = await DecideRecipientAsync(context, to, recipientDomain, isNullSender, senderDomain, cancellationToken);
        if (decision is null)
            return false;

        // Accepted: count it against the message cap and the sender's and user's budgets.
        context.Properties[SessionKeys.RecipientCount] = accepted + 1;
        if (decision.Value)
            context.Properties[SessionKeys.HasHostedRecipient] = true;

        if (!isNullSender)
            await _rateLimiter.RecordSenderRecipientsAsync(sender, 1, cancellationToken);

        if (context.Properties.TryGetValue(SessionKeys.RelayUser, out var u) && u is RelayUser user)
            _rateLimiter.RecordUserRecipients(UserRateKey(user), user.RateLimitPerMinute, user.RateLimitPerDay);

        return true;
    }

    /// <summary>
    /// The recipient gates. Returns true for a hosted (or backup-MX) recipient, false for an external
    /// recipient the client may relay to, and null when the recipient was refused (already recorded).
    /// </summary>
    private async Task<bool?> DecideRecipientAsync(
        ISessionContext context, IMailbox to, string recipientDomain, bool isNullSender, string senderDomain,
        CancellationToken cancellationToken)
    {
        // ---- Mail for a domain we HOST (inbound) ----
        // Backup-MX domains are hosted (read live from the runtime config cache).
        var backupMx = await _configCache.GetBackupMxSettingsAsync(cancellationToken);
        if (backupMx.Enabled &&
            backupMx.DomainList.Any(d => string.Equals(d, recipientDomain, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Configured accepted recipient domains are hosted (inbound).
        var acceptedDomains = await _configCache.GetAcceptedDomainsAsync(cancellationToken);
        var isHostedRecipient = acceptedDomains.Any(d =>
            string.Equals(d, recipientDomain, StringComparison.OrdinalIgnoreCase));
        if (isHostedRecipient)
        {
            // Optional verification gate: when enabled, an accepted recipient domain must also have its
            // ownership verified (DNS TXT) before the relay accepts mail for it.
            var emailAuth = await _configCache.GetEmailAuthSettingsAsync(cancellationToken);
            if (emailAuth.RequireRecipientDomainVerification)
            {
                var verified = await _configCache.GetVerifiedRecipientDomainsAsync(cancellationToken);
                if (!verified.Contains(recipientDomain))
                {
                    _logger.LogWarning("Recipient {Recipient} rejected: domain {Domain} ownership not verified (verification required)",
                        to.AsAddress(), recipientDomain);
                    Reject(context, RejectReason.RecipientDomainNotVerified, senderDomain,
                        $"recipient domain {recipientDomain}");
                    return null;
                }
            }
            return true;
        }

        // ---- An EXTERNAL recipient (a domain we do not host) ----
        // A null sender (bounce / auto-reply) is accepted only for domains we host: relaying it onwards
        // would let anyone reflect backscatter at third parties through us, whatever the client's rights.
        if (isNullSender)
        {
            _logger.LogWarning("Null-sender message to external recipient {Recipient} refused: bounces are accepted only for hosted domains",
                to.AsAddress());
            Reject(context, RejectReason.NullSenderRelayDenied, senderDomain, $"recipient domain {recipientDomain}");
            return null;
        }

        // Open-relay protection (ALWAYS ON — not configurable): relay externally only for an
        // authenticated session, or a client matched by an EXPLICIT, non-"any" allow-IP rule. An empty
        // configuration, or a single 0.0.0.0/0 allow rule, therefore can NEVER turn this into an open
        // relay — the protection cannot be disabled or bypassed by configuration alone.
        if (GetAuthenticatedUser(context) is not null)
            return false;

        var remoteEndPoint = context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep) ? ep as IPEndPoint : null;
        var relayTenantId = context.Properties.TryGetValue(SessionKeys.TenantId, out var tid) && tid is int t
            ? t
            : TenantDefaults.DefaultTenantId;
        var ipRules = await _configCache.GetIpAccessRulesAsync(cancellationToken);

        if (remoteEndPoint is not null &&
            IpAccessEvaluator.IsExplicitlyAllowedForRelay(remoteEndPoint.Address, ipRules, relayTenantId, _options.AllowedNetworks))
        {
            return false;
        }

        _logger.LogWarning(
            "Relaying denied for {Recipient} from {ClientIp}: relaying to an external domain requires SMTP authentication or an explicit allow-IP rule (open-relay protection)",
            to.AsAddress(), remoteEndPoint?.Address);
        Reject(context, RejectReason.OpenRelayDenied, senderDomain, $"recipient domain {recipientDomain}");
        return null;
    }

    /// <summary>
    /// The accepted-sender-domain gate (and its optional ownership-verification gate) for one domain, shared
    /// by the envelope check here and the header-From check in <see cref="RelayMessageStore"/>. Returns the
    /// refusing reason, or null when the domain may send. An empty accepted list accepts every domain.
    /// </summary>
    internal async Task<RejectReason?> CheckSenderDomainAsync(string domain, CancellationToken cancellationToken)
    {
        var acceptedSenderDomains = await _configCache.GetAcceptedSenderDomainsAsync(cancellationToken);
        if (acceptedSenderDomains.Count == 0)
            return null;

        if (!acceptedSenderDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
            return RejectReason.SenderDomainNotAccepted;

        // Optional verification gate: when enabled, an accepted sender domain must also have its
        // ownership verified (DNS TXT) before it may send. Applies only among configured domains.
        var emailAuth = await _configCache.GetEmailAuthSettingsAsync(cancellationToken);
        if (emailAuth.RequireSenderDomainVerification)
        {
            var verified = await _configCache.GetVerifiedSenderDomainsAsync(cancellationToken);
            if (!verified.Contains(domain))
                return RejectReason.SenderDomainNotVerified;
        }

        return null;
    }

    internal static string? GetAuthenticatedUser(ISessionContext context)
    {
        return context.Properties.TryGetValue(SessionKeys.AuthenticatedUser, out var user)
            ? user as string
            : null;
    }

    /// <summary>The user's SendAs allow-list permits <paramref name="senderAddress"/> (no list = no restriction).</summary>
    internal static bool IsAllowedSender(RelayUser? user, string senderAddress)
    {
        if (user?.AllowedSenderAddresses is null or "")
            return true; // no restriction

        var allowed = user.AllowedSenderAddresses.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Any(a => string.Equals(a, senderAddress, StringComparison.OrdinalIgnoreCase));
    }

    private static string UserRateKey(RelayUser user) => $"user:{user.Id}";

    private async Task<RelayUser?> GetUserAsync(string username, int tenantId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        return await userService.GetByUsernameAsync(username, tenantId, cancellationToken);
    }

    /// <summary>The envelope sender as "user@host", or "" for the null sender (<c>MAIL FROM:&lt;&gt;</c>).</summary>
    internal static string SenderAddress(IMailbox? mailbox) =>
        mailbox is null ? "" : mailbox.AsAddress() ?? "";

    internal static string GetDomainFromAddress(string emailAddress)
    {
        var atIndex = emailAddress.LastIndexOf('@');
        return atIndex >= 0 ? emailAddress[(atIndex + 1)..] : emailAddress;
    }
}

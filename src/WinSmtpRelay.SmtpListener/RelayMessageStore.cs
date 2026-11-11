using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;
using WinSmtpRelay.Security.Models;

namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// Accepts the message body at the end of DATA: enforces the size limit and the content gates that need
/// the message itself (header From, hop count, SPF/DMARC), adds the trace headers, and queues it. The 250
/// is only sent once the message is committed to the queue.
/// </summary>
public class RelayMessageStore : MessageStore
{
    /// <summary>The authserv-id of the Authentication-Results header this relay adds (RFC 8601 §2.5).</summary>
    public const string AuthServId = "winsmtprelay";

    private static readonly SmtpResponse MessageTooLarge =
        new(SmtpReplyCode.SizeLimitExceeded, "5.3.4 Message size exceeds fixed maximum message size");

    private static readonly SmtpResponse MultipleFrom =
        new(SmtpReplyCode.MailboxUnavailable, "5.7.1 Message rejected: more than one From header");

    private static readonly SmtpResponse HeaderFromRejected =
        new(SmtpReplyCode.MailboxUnavailable, "5.7.1 Message rejected: sender address in From header not allowed");

    private static readonly SmtpResponse Unparsable =
        new(SmtpReplyCode.TransactionFailed, "5.6.0 Message could not be parsed");

    private static readonly SmtpResponse TooManyHops =
        new(SmtpReplyCode.TransactionFailed, "5.4.6 Too many hops, mail loop detected");

    private static readonly SmtpResponse PolicyRejected =
        new(SmtpReplyCode.MailboxUnavailable, "5.7.1 Message rejected due to DMARC/SPF policy");

    private static readonly SmtpResponse PolicyDeferred =
        new(SmtpReplyCode.Aborted, "4.7.24 SPF/DMARC could not be evaluated (DNS error), try again later");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EmailAuthenticationService _emailAuth;
    private readonly RelayMailboxFilter _mailboxFilter;
    private readonly IRuntimeConfigCache _configCache;
    private readonly IRejectionRecorder _rejectionRecorder;
    private readonly WebhookService _webhookService;
    private readonly IActivityNotifier _activityNotifier;
    private readonly SmtpListenerOptions _options;
    private readonly DnsOptions _dnsOptions;
    private readonly ILogger<RelayMessageStore> _logger;

    public RelayMessageStore(
        IServiceScopeFactory scopeFactory,
        EmailAuthenticationService emailAuth,
        RelayMailboxFilter mailboxFilter,
        IRuntimeConfigCache configCache,
        IRejectionRecorder rejectionRecorder,
        WebhookService webhookService,
        IActivityNotifier activityNotifier,
        IOptions<SmtpListenerOptions> options,
        IOptions<DnsOptions> dnsOptions,
        ILogger<RelayMessageStore> logger)
    {
        _scopeFactory = scopeFactory;
        _emailAuth = emailAuth;
        _mailboxFilter = mailboxFilter;
        _configCache = configCache;
        _rejectionRecorder = rejectionRecorder;
        _webhookService = webhookService;
        _activityNotifier = activityNotifier;
        _options = options.Value;
        _dnsOptions = dnsOptions.Value;
        _logger = logger;
    }

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        var sender = RelayMailboxFilter.SenderAddress(transaction.From);
        var isNullSender = sender.Length == 0;
        var recipients = string.Join(";", transaction.To.Select(m => m.AsAddress()));
        var envelopeFromDomain = isNullSender ? "" : RelayMailboxFilter.GetDomainFromAddress(sender);

        var tenantId = context.Properties.TryGetValue(SessionKeys.TenantId, out var tenantObj) && tenantObj is int resolvedTenant
            ? resolvedTenant
            : TenantDefaults.DefaultTenantId;

        var remoteEndPoint = context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            ? ep as IPEndPoint
            : null;
        var sourceIp = remoteEndPoint?.Address.ToString();
        var authenticatedUser = RelayMailboxFilter.GetAuthenticatedUser(context);

        // The size limit applies to the bytes received, whatever SIZE= the client declared (or did not).
        // The session pipe already stops a body that grows far beyond it; this is the exact check.
        if (buffer.Length > _options.MaxMessageSizeBytes)
        {
            _logger.LogWarning("Message from {Sender} ({ClientIp}) rejected: {Size} bytes exceeds limit {Limit}",
                sender, sourceIp, buffer.Length, _options.MaxMessageSizeBytes);
            return Refuse(context, RejectReason.MessageTooLarge, MessageTooLarge, envelopeFromDomain,
                $"{buffer.Length} bytes exceeds the {_options.MaxMessageSizeBytes}-byte limit");
        }

        // Parse once, straight from the session buffer (no extra copy). MimeKit is lenient; a failure here
        // is rare and only matters where a content gate needs the parsed headers (see below).
        MimeMessage? parsed = null;
        try
        {
            parsed = await MimeMessage.LoadAsync(new ReadOnlySequenceStream(buffer), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not parse a message from {Sender}", sender);
        }

        var headers = RawHeaderBlock.Read(buffer);
        var messageId = ExtractMessageId(parsed, headers) ?? $"<{Guid.NewGuid()}@winsmtprelay>";

        // Counted on both readings of the header block, so a line that makes one reader stop early (and the
        // other carry on) cannot hide a second From or the Received trail from the gates below.
        var fromCount = Math.Max(headers.Count("From"), parsed?.Headers.Count(h => h.Id == HeaderId.From) ?? 0);
        var hops = Math.Max(headers.Count("Received"), parsed?.Headers.Count(h => h.Id == HeaderId.Received) ?? 0);

        // ---- Content gates ----

        // Two From headers would let a message show one sender while DKIM signs (and DMARC checks) the
        // other; RFC 5322 §3.6 allows exactly one.
        if (fromCount > 1)
        {
            _logger.LogWarning("Message {MessageId} from {Sender} rejected: more than one From header", messageId, sender);
            return Refuse(context, RejectReason.MultipleFromHeaders, MultipleFrom, envelopeFromDomain);
        }

        var headerGate = await CheckHeaderFromAsync(context, parsed, fromCount, isNullSender, cancellationToken);
        if (headerGate is { } refusal)
        {
            _logger.LogWarning("Message {MessageId} from {Sender} rejected: header From not allowed ({Reason})",
                messageId, sender, refusal.Reason);
            return Refuse(context, refusal.Reason, refusal.Response, refusal.Domain ?? envelopeFromDomain, refusal.Detail);
        }

        if (_options.MaxReceivedHeaders > 0 && hops > _options.MaxReceivedHeaders)
        {
            _logger.LogWarning("Message {MessageId} from {Sender} rejected: {Hops} Received headers exceed the hop limit {Limit}",
                messageId, sender, hops, _options.MaxReceivedHeaders);
            return Refuse(context, RejectReason.HopLimitExceeded, TooManyHops, envelopeFromDomain, $"{hops} Received headers");
        }

        // ---- SPF / DMARC ----
        // Only for mail that ARRIVES at a domain we host from a peer that did not log in: authenticated
        // submission and devices on our own networks are not SMTP peers whose SPF/DMARC means anything, and
        // evaluating them would stamp internal addresses into the headers of mail that leaves the network.
        AuthenticationResults? authResults = null;
        if (await ShouldEvaluateSenderAuthenticationAsync(context, remoteEndPoint, authenticatedUser, cancellationToken))
        {
            var headerFromDomain = parsed?.From.Mailboxes.FirstOrDefault()?.Domain;
            if (string.IsNullOrWhiteSpace(headerFromDomain))
                headerFromDomain = envelopeFromDomain;

            authResults = await _emailAuth.CheckAllAsync(
                remoteEndPoint!.Address, envelopeFromDomain, headerFromDomain, parsed, cancellationToken);

            if (await _emailAuth.ShouldRejectAsync(authResults, cancellationToken))
            {
                var dmarcRejected = authResults.Dmarc.Verdict == DmarcVerdict.Fail;
                _logger.LogWarning("Message {MessageId} rejected: {Check} policy failure (from {Sender})",
                    messageId, dmarcRejected ? "DMARC" : "SPF", sender);
                return Refuse(context, dmarcRejected ? RejectReason.DmarcReject : RejectReason.SpfFail, PolicyRejected,
                    dmarcRejected ? headerFromDomain : envelopeFromDomain);
            }

            if (await _emailAuth.ShouldDeferAsync(authResults, cancellationToken))
            {
                _logger.LogWarning("Message {MessageId} deferred: SPF failed and the DMARC record of {Domain} could not be looked up",
                    messageId, headerFromDomain);
                return Refuse(context, RejectReason.EmailAuthTempError, PolicyDeferred, headerFromDomain);
            }
        }

        var rawMessage = BuildQueuedMessage(context, buffer, headers, authResults, authenticatedUser);

        using var scope = _scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();

        var message = new QueuedMessage
        {
            MessageId = messageId,
            Sender = sender,
            Recipients = recipients,
            RawMessage = rawMessage,
            SizeBytes = rawMessage.Length,
            SourceIp = sourceIp,
            AuthenticatedUser = authenticatedUser,
            TenantId = tenantId,
            NextRetryUtc = DateTimeOffset.UtcNow
        };

        var id = await queue.EnqueueAsync(message, cancellationToken);

        _logger.LogInformation(
            "Message {MessageId} queued (id={QueueId}) from {Sender} to {Recipients} ({Size} bytes) via {SourceIp} spf={SpfVerdict}",
            messageId, id, isNullSender ? "<>" : sender, recipients, rawMessage.Length, sourceIp ?? "unknown",
            authResults?.Spf.Verdict.ToString() ?? "not checked");

        // Fire notifications (fire-and-forget, do not block SMTP response)
        _ = _webhookService.NotifyMessageReceivedAsync(messageId, sender, recipients, rawMessage.Length, sourceIp, CancellationToken.None);
        _ = _activityNotifier.NotifyMessageReceivedAsync(messageId, sender, recipients, rawMessage.Length, tenantId);

        return SmtpResponse.Ok;
    }

    private SmtpResponse Refuse(ISessionContext context, RejectReason reason, SmtpResponse response, string? senderDomain, string? detail = null)
    {
        var clientIp = (context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            ? ep as IPEndPoint
            : null)?.Address.ToString();
        var tenantId = context.Properties.TryGetValue(SessionKeys.TenantId, out var tid) && tid is int t ? t : (int?)null;

        _rejectionRecorder.Record(clientIp, reason, (int)response.ReplyCode, senderDomain, tenantId, detail);
        return response;
    }

    private readonly record struct HeaderFromRefusal(RejectReason Reason, SmtpResponse Response, string? Domain, string? Detail);

    /// <summary>
    /// Applies the envelope's sender gates to the header From, which is what the recipient sees and what
    /// DKIM signs: every address in it must be an accepted (and, if required, verified) sender domain, and
    /// an authenticated user with a SendAs list may only use addresses on it. A message without a From
    /// header passes (nothing is shown or signed as anyone). Bounces (null sender) are exempt, as at MAIL:
    /// their From is the reporting system's, and they may only address hosted domains.
    /// </summary>
    private async Task<HeaderFromRefusal?> CheckHeaderFromAsync(
        ISessionContext context, MimeMessage? parsed, int fromCount, bool isNullSender, CancellationToken cancellationToken)
    {
        if (isNullSender || fromCount == 0)
            return null;

        context.Properties.TryGetValue(SessionKeys.RelayUser, out var u);
        var user = u as RelayUser;
        var sendAsRestricted = !string.IsNullOrEmpty(user?.AllowedSenderAddresses);
        var domainsRestricted = (await _configCache.GetAcceptedSenderDomainsAsync(cancellationToken)).Count > 0;

        if (!sendAsRestricted && !domainsRestricted)
            return null;

        // A gate is configured, so an unreadable From must not slip through it.
        var mailboxes = parsed?.From.Mailboxes.ToList();
        if (mailboxes is null || mailboxes.Count == 0)
            return new HeaderFromRefusal(RejectReason.SenderDomainNotAccepted, parsed is null ? Unparsable : HeaderFromRejected,
                null, "header From could not be read");

        foreach (var mailbox in mailboxes)
        {
            var domain = mailbox.Domain ?? "";
            var domainGate = await _mailboxFilter.CheckSenderDomainAsync(domain, cancellationToken);
            if (domainGate is not null)
                return new HeaderFromRefusal(domainGate.Value, HeaderFromRejected, domain, "header From");

            if (sendAsRestricted && !RelayMailboxFilter.IsAllowedSender(user, mailbox.Address))
                return new HeaderFromRefusal(RejectReason.SendAsDenied, HeaderFromRejected, domain,
                    $"user {RelayMailboxFilter.GetAuthenticatedUser(context)}, header From");
        }

        return null;
    }

    private async Task<bool> ShouldEvaluateSenderAuthenticationAsync(
        ISessionContext context, IPEndPoint? remoteEndPoint, string? authenticatedUser, CancellationToken cancellationToken)
    {
        if (authenticatedUser is not null || remoteEndPoint is null)
            return false;

        if (!context.Properties.ContainsKey(SessionKeys.HasHostedRecipient))
            return false;

        var address = remoteEndPoint.Address.IsIPv4MappedToIPv6 ? remoteEndPoint.Address.MapToIPv4() : remoteEndPoint.Address;
        if (IsInternalAddress(address))
            return false;

        var ipRules = await _configCache.GetIpAccessRulesAsync(cancellationToken);
        return !IpAccessEvaluator.IsTrustedSource(address, ipRules, _options.AllowedNetworks);
    }

    /// <summary>Loopback, RFC 1918, link-local, CGNAT (RFC 6598) and IPv6 unique-local addresses.</summary>
    internal static bool IsInternalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    /// <summary>
    /// Assembles the queued bytes in ONE allocation: our Received and Authentication-Results headers, the
    /// original header block without any Authentication-Results that claims our authserv-id (a sender
    /// could otherwise forge our verdict, RFC 8601 §5), and the body copied straight from the session buffer.
    /// </summary>
    private byte[] BuildQueuedMessage(
        ISessionContext context, ReadOnlySequence<byte> buffer, RawHeaderBlock headers, AuthenticationResults? authResults,
        string? authenticatedUser)
    {
        var prefix = new StringBuilder();
        prefix.Append(BuildReceivedHeader(context, authenticatedUser));

        if (authResults is not null && HasVerdict(authResults))
            prefix.Append("Authentication-Results: ").Append(authResults.ToHeaderValue(AuthServId)).Append("\r\n");

        var prefixBytes = Encoding.ASCII.GetBytes(prefix.ToString());

        var stripped = headers.Fields
            .Where(f => f.Name.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase) && ClaimsOurAuthServId(f.Value))
            .ToList();
        var strippedLength = stripped.Sum(f => f.End - f.Start);

        var bodyLength = buffer.Length - headers.Length;
        var result = new byte[prefixBytes.Length + headers.Length - strippedLength + bodyLength];
        var span = result.AsSpan();

        prefixBytes.CopyTo(span);
        var pos = prefixBytes.Length;

        var copyFrom = 0;
        foreach (var field in stripped)
        {
            headers.Bytes.AsSpan(copyFrom, field.Start - copyFrom).CopyTo(span[pos..]);
            pos += field.Start - copyFrom;
            copyFrom = field.End;
        }

        headers.Bytes.AsSpan(copyFrom).CopyTo(span[pos..]);
        pos += headers.Length - copyFrom;

        buffer.Slice(headers.Length).CopyTo(span[pos..]);

        if (stripped.Count > 0)
            _logger.LogInformation("Removed {Count} incoming Authentication-Results header(s) claiming authserv-id {AuthServId}",
                stripped.Count, AuthServId);

        return result;
    }

    private static bool HasVerdict(AuthenticationResults results) =>
        results.Spf.Verdict != SpfVerdict.None
        || results.Dmarc.Verdict != DmarcVerdict.None
        || (results.Dkim is not null && results.Dkim.Verdict != DkimVerdict.None);

    /// <summary>True when an Authentication-Results value starts with our authserv-id (RFC 8601 §2.2).</summary>
    internal static bool ClaimsOurAuthServId(string value)
    {
        var v = value.TrimStart();
        if (!v.StartsWith(AuthServId, StringComparison.OrdinalIgnoreCase))
            return false;

        return v.Length == AuthServId.Length || v[AuthServId.Length] is ';' or ' ' or '\t' or '(' or '\r' or '\n';
    }

    /// <summary>
    /// The trace header (RFC 5321 §4.4) — also what the hop limit counts on the next relay. The client's
    /// HELO name and address appear only for an unauthenticated peer on a public address (an inbound MTA);
    /// for authenticated submission and internal devices they are left out, so internal host names and
    /// addresses do not travel with outbound mail. No tenant name, no machine name: "by" is the configured
    /// public host name, or the relay's product name.
    /// </summary>
    private string BuildReceivedHeader(ISessionContext context, string? authenticatedUser)
    {
        var secure = context.Pipe?.IsSecure == true;
        var ehlo = !context.Properties.TryGetValue(SessionKeys.UsedEhlo, out var e) || e is true;
        var protocol = (ehlo ? "ESMTP" : "SMTP") + (ehlo && secure ? "S" : "") + (ehlo && authenticatedUser is not null ? "A" : "");

        var by = string.IsNullOrWhiteSpace(_dnsOptions.PublicHostname) ? "WinSmtpRelay" : SanitizeDomain(_dnsOptions.PublicHostname);

        var remote = (context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep) ? ep as IPEndPoint : null)?.Address;
        if (remote is not null && remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();

        var sb = new StringBuilder("Received: ");
        if (authenticatedUser is null && remote is not null && !IsInternalAddress(remote))
        {
            var helo = context.Properties.TryGetValue(SessionKeys.HeloName, out var h) ? SanitizeDomain(h as string) : "unknown";
            var literal = remote.AddressFamily == AddressFamily.InterNetworkV6 ? $"IPv6:{remote}" : remote.ToString();
            sb.Append("from ").Append(helo).Append(" ([").Append(literal).Append("])\r\n\t");
        }

        sb.Append("by ").Append(by).Append(" (WinSmtpRelay) with ").Append(protocol).Append(";\r\n\t")
          .Append(DateUtils.FormatDate(DateTimeOffset.UtcNow)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Keeps a HELO or host name to the characters a domain or address literal may contain.</summary>
    internal static string SanitizeDomain(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unknown";

        var sb = new StringBuilder(Math.Min(name.Length, 255));
        foreach (var c in name)
        {
            if (sb.Length >= 255)
                break;
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']')
                sb.Append(c);
        }

        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    /// <summary>
    /// The top-level Message-ID only — never text from the body. Taken from MimeKit's parse, or from the raw
    /// header block when the parse failed; CR/LF and whitespace are removed and the length capped, because
    /// the value is stored, logged and shown in the UI.
    /// </summary>
    internal static string? ExtractMessageId(MimeMessage? parsed, RawHeaderBlock headers)
    {
        string? id = parsed?.MessageId;
        if (string.IsNullOrWhiteSpace(id))
        {
            var raw = headers.First("Message-ID")?.Value;
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var start = raw.IndexOf('<');
            var end = start >= 0 ? raw.IndexOf('>', start + 1) : -1;
            id = start >= 0 && end > start ? raw[(start + 1)..end] : raw;
        }

        var clean = new string(id.Where(c => c > ' ' && c < 0x7F && c is not '<' and not '>').ToArray());
        if (clean.Length == 0)
            return null;
        if (clean.Length > 250)
            clean = clean[..250];

        return string.Create(CultureInfo.InvariantCulture, $"<{clean}>");
    }
}

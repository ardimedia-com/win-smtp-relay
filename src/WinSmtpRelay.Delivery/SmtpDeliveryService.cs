using System.Net;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Delivery;

public class SmtpDeliveryService : IDeliveryService
{
    private readonly IMxResolver _mxResolver;
    private readonly DeliveryOptions _config;
    private readonly IRuntimeConfigCache _configCache;
    private readonly DkimSigningService _dkimSigner;
    private readonly IDkimDomainService _dkimDomains;
    private readonly IPublicSuffixService _psl;
    private readonly IDnsSettingsService _dnsSettings;
    private readonly ILogger<SmtpDeliveryService> _logger;

    public SmtpDeliveryService(
        IMxResolver mxResolver,
        IOptions<DeliveryOptions> options,
        IRuntimeConfigCache configCache,
        DkimSigningService dkimSigner,
        IDkimDomainService dkimDomains,
        IPublicSuffixService psl,
        IDnsSettingsService dnsSettings,
        ILogger<SmtpDeliveryService> logger)
    {
        _mxResolver = mxResolver;
        _config = options.Value;
        _configCache = configCache;
        _dkimSigner = dkimSigner;
        _dkimDomains = dkimDomains;
        _psl = psl;
        _dnsSettings = dnsSettings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DeliveryResult>> DeliverAsync(QueuedMessage message, CancellationToken cancellationToken = default)
    {
        var mimeMessage = await MimeMessage.LoadAsync(new MemoryStream(message.RawMessage), cancellationToken);

        // Pin the wire encoding BEFORE DKIM-signing (MimeKit's documented DKIM prerequisite). Without
        // this, MailKit re-encodes 8-bit bodies to 7-bit at send time when the receiving server does not
        // advertise 8BITMIME — changing the body AFTER the signature was computed, so the DKIM body hash
        // (bh=) no longer matches and DMARC fails at exactly the strict/legacy receivers. Preparing to
        // 7-bit up front makes the signed bytes immutable for every receiver.
        mimeMessage.Prepare(EncodingConstraint.SevenBit);

        // DKIM-sign before sending, scoped to the message's tenant so a tenant can only sign with
        // its own key (no-op if the tenant has no DKIM key for the sender domain).
        var senderDomain = mimeMessage.From.Mailboxes.FirstOrDefault()?.Domain;
        var tenantDkim = senderDomain is null
            ? null
            : await _dkimDomains.GetForSigningAsync(message.TenantId, senderDomain, cancellationToken);
        _dkimSigner.Sign(mimeMessage, message.TenantId, tenantDkim);

        // Envelope-from (Return-Path) vs. From-header alignment. SPF authenticates the envelope-from domain,
        // but DMARC requires it to ALIGN with the From domain — a green SPF record on a different bounce
        // domain still fails DMARC unless DKIM covers it. Surface the gap, and optionally realign the
        // transmitted MAIL FROM (opt-in; the stored message is never changed).
        var envelopeSender = ResolveEnvelopeSender(message.Sender, senderDomain, tenantDkim is not null);

        // Optional per-tenant outbound source IP (null = OS default).
        var egressEndPoint = ParseEgressEndPoint(await _configCache.GetTenantEgressIpAsync(message.TenantId, cancellationToken));

        // The host-level EHLO fallback (Settings → Sending identity → Public hostname). Per-connector
        // overrides take precedence; the effective name is resolved per delivery path below.
        var publicHostname = (await _dnsSettings.GetAsync(cancellationToken)).PublicHostname;

        // Skip recipients that already received a 250 on a previous attempt — never re-deliver to them
        // when retrying a partially-failed multi-recipient/multi-domain message.
        var alreadyDelivered = new HashSet<string>(
            (message.DeliveredRecipients ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        var recipients = message.Recipients
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => !alreadyDelivered.Contains(r))
            .ToArray();
        var results = new List<DeliveryResult>();

        if (recipients.Length == 0)
            return results; // everything already delivered on a prior attempt → nothing to do

        // Group recipients by domain for efficient delivery
        var byDomain = recipients.GroupBy(r => r.Split('@').Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var domainGroup in byDomain)
        {
            var domain = domainGroup.Key;
            var domainRecipients = domainGroup.ToList();

            var domainResults = await DeliverToDomainAsync(mimeMessage, envelopeSender, domainRecipients, domain, message.TenantId, egressEndPoint, publicHostname, cancellationToken);
            results.AddRange(domainResults);
        }

        // If any recipient failed, throw so DeliveryWorker can handle retry logic
        var failures = results.Where(r => !r.Success).ToList();
        if (failures.Count > 0)
        {
            throw new DeliveryException(
                $"Delivery failed for {failures.Count} recipient(s): {string.Join("; ", failures.Select(f => $"{f.Recipient}: {f.StatusCode} {f.StatusMessage}"))}",
                results);
        }

        return results;
    }

    private async Task<List<DeliveryResult>> DeliverToDomainAsync(
        MimeMessage mimeMessage,
        string sender,
        List<string> recipients,
        string domain,
        int tenantId,
        IPEndPoint? egressEndPoint,
        string? publicHostname,
        CancellationToken cancellationToken)
    {
        // 1. Per-domain route takes highest priority (scoped to the message's tenant so a tenant
        // cannot define a route that captures another tenant's outbound mail)
        var route = await FindDomainRouteAsync(domain, tenantId, cancellationToken);
        if (route is { SendConnector: not null })
        {
            var connector = route.SendConnector;
            _logger.LogDebug("Using domain route {Pattern} via connector {Connector} for domain {Domain}",
                route.DomainPattern, connector.Name, domain);
            return await SendViaSmtpAsync(
                mimeMessage, sender, recipients,
                connector.SmartHost ?? "", connector.SmartHostPort,
                connector.Username, connector.EncryptedPassword,
                connector.OpportunisticTls, connector.RequireTls,
                connector.ConnectTimeoutSeconds,
                egressEndPoint,
                RequireEhloDomain(connector.EhloDomain, publicHostname),
                cancellationToken);
        }

        // 2. The tenant's default send connector (a connector flagged "Default" for this tenant), if it
        // routes through a smart host. A direct-MX default connector falls through to direct MX below.
        var defaultConnector = await _configCache.GetDefaultConnectorAsync(tenantId, cancellationToken);
        if (defaultConnector is { SmartHost: { Length: > 0 } smartHost })
        {
            _logger.LogDebug("Using default connector {Connector} for domain {Domain}",
                defaultConnector.Name, domain);
            return await SendViaSmtpAsync(
                mimeMessage, sender, recipients,
                smartHost, defaultConnector.SmartHostPort,
                defaultConnector.Username, defaultConnector.EncryptedPassword,
                defaultConnector.OpportunisticTls, defaultConnector.RequireTls,
                defaultConnector.ConnectTimeoutSeconds,
                egressEndPoint,
                RequireEhloDomain(defaultConnector.EhloDomain, publicHostname),
                cancellationToken);
        }

        // 3. Global smart host (appsettings)
        if (!string.IsNullOrWhiteSpace(_config.SmartHost))
        {
            return await SendViaSmtpAsync(
                mimeMessage, sender, recipients,
                _config.SmartHost, _config.SmartHostPort,
                _config.SmartHostUsername, _config.SmartHostPassword,
                _config.OpportunisticTls, _config.RequireTls,
                _config.ConnectTimeoutSeconds,
                egressEndPoint,
                RequireEhloDomain(null, publicHostname),
                cancellationToken);
        }

        // 4. Direct MX delivery
        var ehloDomain = RequireEhloDomain(null, publicHostname);
        var mxHosts = await _mxResolver.ResolveMxAsync(domain, cancellationToken);

        Exception? lastException = null;
        string? rejectedByHost = null; // the MX that rejected us at SMTP protocol level, if any
        foreach (var mxHost in mxHosts)
        {
            try
            {
                return await SendViaSmtpAsync(mimeMessage, sender, recipients, mxHost, 25, null, null,
                    _config.OpportunisticTls, requireTls: false, _config.ConnectTimeoutSeconds, egressEndPoint, ehloDomain, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The SERVICE is shutting down (outer token cancelled) — not a delivery failure. Propagate
                // so DeliveryWorker requeues the message cleanly instead of "trying next", wrapping it as a
                // bounce, and logging a bogus failed attempt. (A per-attempt connect TIMEOUT cancels only
                // the inner linked token, so cancellationToken.IsCancellationRequested is false there and we
                // fall through to the next MX as before.)
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (ex is SmtpCommandException)
                    rejectedByHost = mxHost;
                _logger.LogWarning(ex, "Delivery to MX {MxHost} failed for domain {Domain}, trying next",
                    mxHost, domain);
            }
        }

        // No MX accepted the message. Two very different failure modes must stay distinguishable in the
        // Journal: an MX that we REACHED and that REJECTED us at SMTP protocol level (permanent, its real
        // 5xx/4xx code and response text), versus no MX being reachable at all (transient 421 — a
        // momentary outage of the destination is retried, not silently bounced).
        var statusCode = lastException is SmtpCommandException sce ? ((int)sce.StatusCode).ToString() : "421";
        var statusMessage = BuildMxFailureMessage(domain, lastException, rejectedByHost, _config.ConnectTimeoutSeconds);
        return recipients.Select(r => new DeliveryResult
        {
            Recipient = r,
            StatusCode = statusCode,
            StatusMessage = statusMessage,
            RemoteServer = rejectedByHost ?? mxHosts.FirstOrDefault()
        }).ToList();
    }

    /// <summary>
    /// Builds the Journal-facing failure text for an unsuccessful MX loop. A protocol-level rejection
    /// names the rejecting host and quotes the server's response ("mx01.example.com rejected the
    /// delivery: …"); anything else is a reachability problem ("No MX host … could be reached").
    /// Conflating the two sends whoever reads the Journal down the wrong debugging path.
    /// </summary>
    internal static string BuildMxFailureMessage(string domain, Exception? lastException, string? rejectedByHost, int connectTimeoutSeconds)
    {
        if (lastException is SmtpCommandException smtpEx)
            return $"{rejectedByHost ?? "The MX host"} rejected the delivery: {smtpEx.Message}";

        // Surface a human-readable reason — the raw OperationCanceledException message is just
        // "A task was canceled", which reads as a mystery in the Journal/Event Log.
        return lastException switch
        {
            null => $"No MX host for domain {domain} could be reached (no usable MX records)",
            OperationCanceledException => $"No MX host for domain {domain} could be reached: the connection timed out after {connectTimeoutSeconds}s",
            _ => $"No MX host for domain {domain} could be reached: {lastException.Message}"
        };
    }

    /// <summary>
    /// Resolves the effective EHLO name for a delivery path (connector override → public hostname →
    /// machine FQDN) and refuses to deliver when none is fully qualified — announcing an unqualified
    /// name gets mail rejected by strict receivers, so failing loudly here beats bouncing in production.
    /// </summary>
    private static string RequireEhloDomain(string? connectorEhloDomain, string? publicHostname) =>
        EhloHostname.Resolve(connectorEhloDomain, publicHostname)
        ?? throw new EhloNotConfiguredException();

    /// <summary>
    /// Returns the envelope sender (MAIL FROM) to transmit. When the stored envelope-from is not aligned with
    /// the From domain it logs the SPF-alignment gap, and — only when <see cref="DeliveryOptions.AlignReturnPath"/>
    /// is enabled — rewrites the domain onto the From domain so SPF aligns for DMARC. An empty return path
    /// (a bounce/DSN, MAIL FROM &lt;&gt;) is never rewritten. The stored message is untouched.
    /// </summary>
    private string ResolveEnvelopeSender(string storedSender, string? headerFromDomain, bool dkimAligned)
    {
        var envelopeDomain = EnvelopeAlignment.DomainOf(storedSender);

        // No From domain, no envelope sender (null return path), or already aligned → nothing to do.
        if (string.IsNullOrWhiteSpace(headerFromDomain) || envelopeDomain is null)
            return storedSender;
        if (EnvelopeAlignment.IsAligned(envelopeDomain, headerFromDomain, _psl))
            return storedSender;

        if (_config.AlignReturnPath && MailboxAddress.TryParse(storedSender, out var mailbox))
        {
            var addr = mailbox.Address;
            var at = addr.LastIndexOf('@');
            var localPart = at > 0 ? addr[..at] : addr;
            if (!string.IsNullOrWhiteSpace(localPart))
            {
                var realigned = $"{localPart}@{headerFromDomain}";
                _logger.LogInformation(
                    "Return-Path realigned from {Old} to {New} for SPF/DMARC alignment (Delivery:AlignReturnPath)",
                    storedSender, realigned);
                return realigned;
            }
        }

        _logger.LogWarning(
            "Envelope-from {Envelope} is not aligned with From domain {From}; SPF will not align for DMARC.{DkimNote} "
                + "Set up DKIM for {From}, or enable Delivery:AlignReturnPath.",
            envelopeDomain, headerFromDomain,
            dkimAligned ? " A DKIM signature aligns, so DMARC still passes." : "");
        return storedSender;
    }

    /// <summary>Parses a configured egress IP into a bindable local endpoint (port 0), or null if unset/invalid.</summary>
    internal static IPEndPoint? ParseEgressEndPoint(string? egressIp)
    {
        if (string.IsNullOrWhiteSpace(egressIp))
            return null;
        return IPAddress.TryParse(egressIp.Trim(), out var address)
            ? new IPEndPoint(address, 0)
            : null;
    }

    /// <summary>
    /// Maps a connector's TLS flags to a MailKit transport option. <c>requireTls</c> enforces
    /// encryption (mandatory STARTTLS — MailKit fails the connection if the server won't negotiate TLS)
    /// and takes precedence; <c>opportunisticTls</c> upgrades to STARTTLS only when the server offers it;
    /// neither flag means an unencrypted connection. (Implicit TLS / port 465 is not handled — submission
    /// via STARTTLS on 587 is the supported smart-host path; direct-MX delivery always stays
    /// opportunistic so it can still reach MX hosts that offer no TLS.)
    /// </summary>
    internal static SecureSocketOptions ResolveTlsOption(bool opportunisticTls, bool requireTls) =>
        requireTls ? SecureSocketOptions.StartTls
        : opportunisticTls ? SecureSocketOptions.StartTlsWhenAvailable
        : SecureSocketOptions.None;

    internal async Task<DomainRoute?> FindDomainRouteAsync(string domain, int tenantId, CancellationToken ct = default)
    {
        var routes = await _configCache.GetDomainRoutesAsync(ct);
        foreach (var route in routes)
        {
            if (route.TenantId != tenantId) continue; // only the message's tenant's routes

            var pattern = route.DomainPattern;
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            if (pattern.StartsWith("*."))
            {
                var suffix = pattern[1..]; // ".example.com"
                if (domain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    domain.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase))
                    return route;
            }
            else if (domain.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return route;
            }
        }
        return null;
    }

    private async Task<List<DeliveryResult>> SendViaSmtpAsync(
        MimeMessage mimeMessage,
        string sender,
        List<string> recipients,
        string host,
        int port,
        string? username,
        string? password,
        bool opportunisticTls,
        bool requireTls,
        int connectTimeoutSeconds,
        IPEndPoint? egressEndPoint,
        string ehloDomain,
        CancellationToken cancellationToken)
    {
        // PerRecipientSmtpClient records each recipient's RCPT TO verdict instead of MailKit's default of
        // aborting the whole envelope on the first rejection. Without it, a single bad address (e.g. a
        // non-existent mailbox) fails — and would auto-suppress — every valid co-recipient in the same
        // same-domain transaction.
        using var client = new PerRecipientSmtpClient(new MailKitProtocolLogger(_logger));
        client.Timeout = connectTimeoutSeconds * 1000;

        // Announce a fully qualified EHLO/HELO name. MailKit's default is the bare machine name, which
        // strict receivers reject ("550 Is neither a FQDN nor a IP literal", RFC 5321 §4.1.4).
        client.LocalDomain = ehloDomain;

        // Bind outbound connections to the tenant's source IP when configured.
        if (egressEndPoint is not null)
        {
            client.LocalEndPoint = egressEndPoint;
            _logger.LogDebug("Binding outbound delivery to source IP {EgressIp}", egressEndPoint.Address);
        }

        var tlsOption = ResolveTlsOption(opportunisticTls, requireTls);

        _logger.LogDebug("Connecting to {Host}:{Port} (TLS={TlsOption}, Timeout={Timeout}s)",
            host, port, tlsOption, connectTimeoutSeconds);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSeconds));

        await client.ConnectAsync(host, port, tlsOption, connectCts.Token);

        if (!string.IsNullOrWhiteSpace(username))
            await client.AuthenticateAsync(username, password, cancellationToken);

        var senderAddress = MailboxAddress.Parse(sender);
        var recipientAddresses = recipients.Select(MailboxAddress.Parse).ToList();
        var remoteServer = $"{host}:{port}";

        try
        {
            // MailKit still delivers the DATA payload to the accepted recipients; rejected ones are
            // captured per-recipient in the client.
            await client.SendAsync(mimeMessage, senderAddress, recipientAddresses, cancellationToken);
        }
        catch (AllRecipientsRejectedException)
        {
            // Every recipient was rejected at RCPT TO (none accepted). The per-recipient verdicts are
            // authoritative — sibling MX hosts for a domain share the same recipient set — so surface them
            // per recipient instead of letting the caller fall through and retry the next MX.
        }
        finally
        {
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, cancellationToken); }
                catch { /* best-effort QUIT; the per-recipient verdicts are already captured */ }
            }
        }
        // Any OTHER failure from SendAsync (sender rejected, DATA rejected, dropped connection, timeout) is
        // not recipient-attributable, so it propagates here and the caller tries the next MX as before.

        var results = BuildRecipientResults(recipients, recipientAddresses, client.Rejected, remoteServer);

        var accepted = results.Where(r => r.Success).Select(r => r.Recipient).ToList();
        if (accepted.Count > 0)
            _logger.LogInformation("Delivered to {Recipients} via {Host}:{Port}",
                string.Join(", ", accepted), host, port);
        foreach (var r in results.Where(r => !r.Success))
            _logger.LogWarning("Recipient {Recipient} rejected by {Host}:{Port}: {Code} {Message}",
                r.Recipient, host, port, r.StatusCode, r.StatusMessage);

        return results;
    }

    /// <summary>
    /// Maps the recorded per-recipient RCPT TO verdicts onto <see cref="DeliveryResult"/>s. A recipient
    /// found in <paramref name="rejected"/> carries its real status code and is flagged
    /// <see cref="DeliveryResult.RecipientRejected"/>; every other recipient was accepted and received the
    /// DATA payload (250).
    /// </summary>
    private static List<DeliveryResult> BuildRecipientResults(
        List<string> recipients, List<MailboxAddress> recipientAddresses,
        IReadOnlyDictionary<string, SmtpResponse> rejected, string remoteServer)
    {
        var results = new List<DeliveryResult>(recipients.Count);
        for (var i = 0; i < recipients.Count; i++)
        {
            if (rejected.TryGetValue(recipientAddresses[i].Address, out var reject))
            {
                results.Add(new DeliveryResult
                {
                    Recipient = recipients[i],
                    StatusCode = ((int)reject.StatusCode).ToString(),
                    StatusMessage = string.IsNullOrWhiteSpace(reject.Response)
                        ? $"Recipient rejected ({(int)reject.StatusCode})"
                        : reject.Response,
                    RemoteServer = remoteServer,
                    RecipientRejected = true
                });
            }
            else
            {
                results.Add(new DeliveryResult
                {
                    Recipient = recipients[i],
                    StatusCode = "250",
                    StatusMessage = $"Delivered via {remoteServer}",
                    RemoteServer = remoteServer
                });
            }
        }
        return results;
    }
}

/// <summary>
/// An <see cref="SmtpClient"/> that captures each recipient's RCPT TO verdict instead of MailKit's default
/// behaviour of throwing on the first rejected recipient (which aborts the whole envelope). This enables
/// per-recipient delivery accounting: one bad address no longer fails its valid co-recipients. Follows
/// MailKit's documented pairing — override <see cref="OnRecipientNotAccepted"/> to not throw, and
/// <see cref="OnNoRecipientsAccepted"/> to throw when none are accepted.
/// </summary>
file sealed class PerRecipientSmtpClient(IProtocolLogger protocolLogger) : SmtpClient(protocolLogger)
{
    /// <summary>Recipients the server rejected at RCPT TO, keyed by address (case-insensitive).</summary>
    public Dictionary<string, SmtpResponse> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);

    protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
        => Rejected[mailbox.Address] = response;

    // Since OnRecipientNotAccepted no longer throws, surface the "every recipient rejected" case here so
    // SendAsync does not proceed to DATA with zero recipients (MailKit's documented requirement).
    protected override void OnNoRecipientsAccepted(MimeMessage message)
        => throw new AllRecipientsRejectedException();
}

/// <summary>
/// Thrown by <see cref="PerRecipientSmtpClient"/> when the destination rejected every recipient at RCPT TO.
/// Caught by the delivery service, which then reports the captured per-recipient rejections.
/// </summary>
file sealed class AllRecipientsRejectedException : Exception;

/// <summary>
/// Exception that carries per-recipient delivery results even when some recipients fail.
/// </summary>
public class DeliveryException(string message, IReadOnlyList<DeliveryResult> results)
    : Exception(message)
{
    public IReadOnlyList<DeliveryResult> Results { get; } = results;
}

/// <summary>
/// Thrown before any connection is made when no fully qualified EHLO name can be resolved (no connector
/// override, no public hostname configured, and the machine has no DNS suffix). The delivery worker
/// keeps the message queued — with a loud error, not a bounce — until the operator fixes the
/// configuration; the daily self-check surfaces the same condition as a blocking finding.
/// </summary>
public sealed class EhloNotConfiguredException() : Exception(
    "Outbound delivery is paused: no fully qualified EHLO hostname is available. Set the public hostname "
    + "under Settings → Sending identity (ideally the FQDN matching the PTR of the outbound IP), or an "
    + "EHLO override on the send connector. Announcing this machine's unqualified name would get mail "
    + "rejected by strict receivers (RFC 5321).");

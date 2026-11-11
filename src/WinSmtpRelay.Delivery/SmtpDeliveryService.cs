using System.Net;
using System.Net.Security;
using System.Net.Sockets;
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
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Delivery;

/// <summary>
/// Delivers one message: DKIM-signs it, resolves where each recipient goes (domain route, the tenant's default
/// connector, the global smart host, or direct MX), and sends. Always returns one result per recipient it was
/// asked to deliver — a failure on one destination never discards what another destination already accepted,
/// so the worker can record the delivered recipients and a retry does not send to them twice (H1).
/// </summary>
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

    /// <summary>The SMTP port of direct-MX delivery. Settable only so tests can point it at a local fake server.</summary>
    internal int MxPort { get; set; } = 25;

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
        // Skip recipients that already received a 250 (or failed permanently) on a previous attempt — never
        // re-deliver to them when retrying a partially-failed multi-recipient/multi-domain message.
        var done = new HashSet<string>(
            RecipientList.Split(message.DeliveredRecipients).Concat(RecipientList.Split(message.FailedRecipients)),
            StringComparer.OrdinalIgnoreCase);
        var recipients = RecipientList.Split(message.Recipients).Where(r => !done.Contains(r)).ToList();
        if (recipients.Count == 0)
            return []; // everything already settled on a prior attempt → nothing to do

        var mimeMessage = await MimeMessage.LoadAsync(new MemoryStream(message.RawMessage), cancellationToken);

        // Pin the wire encoding BEFORE DKIM-signing (MimeKit's documented DKIM prerequisite). Without
        // this, MailKit re-encodes 8-bit bodies to 7-bit at send time when the receiving server does not
        // advertise 8BITMIME — changing the body AFTER the signature was computed, so the DKIM body hash
        // (bh=) no longer matches and DMARC fails at exactly the strict/legacy receivers. Preparing to
        // 7-bit up front makes the signed bytes immutable for every receiver.
        mimeMessage.Prepare(EncodingConstraint.SevenBit);

        // DKIM-sign before sending, scoped to the message's tenant so a tenant can only sign with
        // its own key (no-op if the tenant has no DKIM key for the sender domain). A key that exists but cannot
        // be decrypted on this machine fails closed: the message waits (temporary failure) instead of going out
        // unsigned or signed with garbage.
        var senderDomain = mimeMessage.From.Mailboxes.FirstOrDefault()?.Domain;
        DkimDomain? tenantDkim;
        try
        {
            tenantDkim = senderDomain is null
                ? null
                : await _dkimDomains.GetForSigningAsync(message.TenantId, senderDomain, cancellationToken);
        }
        catch (SecretUnavailableException ex)
        {
            _logger.LogError("Message {MessageId} held: {Reason}", message.MessageId, ex.Message);
            return Fail(recipients, "451", $"4.7.5 {ex.Message}", remoteServer: null);
        }
        _dkimSigner.Sign(mimeMessage, message.TenantId, tenantDkim);

        // Envelope-from (Return-Path) vs. From-header alignment. SPF authenticates the envelope-from domain,
        // but DMARC requires it to ALIGN with the From domain — a green SPF record on a different bounce
        // domain still fails DMARC unless DKIM covers it. Surface the gap, and optionally realign the
        // transmitted MAIL FROM (opt-in; the stored message is never changed).
        var envelopeSender = ResolveEnvelopeSender(message.Sender, senderDomain, tenantDkim is not null);

        // Optional per-tenant outbound source IP (null = OS default).
        var egressEndPoint = ParseEgressEndPoint(await _configCache.GetTenantEgressIpAsync(message.TenantId, cancellationToken));

        // The host-level EHLO fallback (Settings → Sending identity → Public hostname) and the host's own public
        // sending addresses (an MX pointing at them is a loop).
        var dnsSettings = await _dnsSettings.GetAsync(cancellationToken);
        var context = new SendContext(mimeMessage, envelopeSender, message.TenantId, egressEndPoint,
            dnsSettings.PublicHostname, OwnPublicAddresses(dnsSettings.SendingIpAddresses));

        // Resolve every recipient's destination first, then send ONE transaction per destination: all recipients
        // that leave through the same smart host share an envelope, whatever their domain; direct MX is per domain.
        var groups = new List<(DeliveryTarget Target, List<string> Recipients)>();
        foreach (var domainGroup in recipients.GroupBy(DomainOf, StringComparer.OrdinalIgnoreCase))
        {
            var target = await ResolveTargetAsync(domainGroup.Key, message.TenantId, cancellationToken);
            var existing = groups.FindIndex(g => g.Target.Key == target.Key);
            if (existing >= 0)
                groups[existing].Recipients.AddRange(domainGroup);
            else
                groups.Add((target, domainGroup.ToList()));
        }

        var results = new List<DeliveryResult>(recipients.Count);
        foreach (var (target, groupRecipients) in groups)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.AddRange(NotAttempted(groupRecipients));
                continue;
            }

            try
            {
                results.AddRange(target.SmartHost is { } smartHost
                    ? await SendViaSmtpAsync(context, groupRecipients, smartHost, cancellationToken)
                    : await DeliverViaMxAsync(context, groupRecipients, target.Domain!, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The SERVICE is stopping. What other destinations already accepted stays in the results; these
                // recipients were not (completely) attempted and go back to the queue without a retry penalty.
                results.AddRange(NotAttempted(groupRecipients));
            }
            catch (Exception ex)
            {
                var remote = target.SmartHost is { } sh ? $"{sh.Host}:{sh.Port}" : null;
                _logger.LogWarning(ex, "Delivery via {Destination} failed for {Count} recipient(s)",
                    remote ?? target.Domain, groupRecipients.Count);
                results.AddRange(FailureResults(groupRecipients, ex, remote, viaSmartHost: target.SmartHost is not null));
            }
        }

        return results;
    }

    /// <summary>Where the recipients of one domain go. Smart-host targets with the same settings share a key.</summary>
    private sealed record DeliveryTarget(string Key, SmartHostSettings? SmartHost, string? Domain);

    /// <summary>The connection settings of one smart host (route connector, default connector or appsettings).</summary>
    internal sealed record SmartHostSettings(
        string Host, int Port, string? Username, string? Password, bool OpportunisticTls, bool RequireTls,
        bool AllowPlaintextAuth, int ConnectTimeoutSeconds, string? EhloOverride);

    /// <summary>Per-message state shared by every destination.</summary>
    private sealed record SendContext(
        MimeMessage Message, string Sender, int TenantId, IPEndPoint? EgressEndPoint, string? PublicHostname,
        IReadOnlyCollection<IPAddress> OwnPublicAddresses);

    private async Task<DeliveryTarget> ResolveTargetAsync(string domain, int tenantId, CancellationToken ct)
    {
        // 1. Per-domain route takes highest priority (scoped to the message's tenant so a tenant
        // cannot define a route that captures another tenant's outbound mail)
        var route = await FindDomainRouteAsync(domain, tenantId, ct);
        if (route is { SendConnector: { } connector })
        {
            _logger.LogDebug("Using domain route {Pattern} via connector {Connector} for domain {Domain}",
                route.DomainPattern, connector.Name, domain);
            return SmartHostTarget(FromConnector(connector));
        }

        // 2. The tenant's default send connector (a connector flagged "Default" for this tenant), if it
        // routes through a smart host. A direct-MX default connector falls through to direct MX below.
        var defaultConnector = await _configCache.GetDefaultConnectorAsync(tenantId, ct);
        if (defaultConnector is { SmartHost.Length: > 0 })
        {
            _logger.LogDebug("Using default connector {Connector} for domain {Domain}", defaultConnector.Name, domain);
            return SmartHostTarget(FromConnector(defaultConnector));
        }

        // 3. Global smart host (appsettings)
        if (!string.IsNullOrWhiteSpace(_config.SmartHost))
        {
            return SmartHostTarget(new SmartHostSettings(
                _config.SmartHost, _config.SmartHostPort, _config.SmartHostUsername, _config.SmartHostPassword,
                _config.OpportunisticTls, _config.RequireTls, _config.SmartHostAllowPlaintextAuth,
                _config.ConnectTimeoutSeconds, EhloOverride: null));
        }

        // 4. Direct MX delivery, one transaction per domain.
        return new DeliveryTarget($"mx:{domain.ToLowerInvariant()}", null, domain);

        static DeliveryTarget SmartHostTarget(SmartHostSettings s) => new(
            $"smarthost:{s.Host.ToLowerInvariant()}:{s.Port}:{s.Username}:{s.OpportunisticTls}:{s.RequireTls}:{s.AllowPlaintextAuth}:{s.EhloOverride}",
            s, null);

        static SmartHostSettings FromConnector(SendConnector c) => new(
            c.SmartHost ?? "", c.SmartHostPort, c.Username, c.EncryptedPassword, c.OpportunisticTls, c.RequireTls,
            c.AllowPlaintextAuth, c.ConnectTimeoutSeconds, c.EhloDomain);
    }

    private Task<List<DeliveryResult>> SendViaSmtpAsync(SendContext ctx, List<string> recipients, SmartHostSettings smartHost, CancellationToken ct)
    {
        // A stored password that cannot be decrypted here (database restored from another machine) must never be
        // sent as the password: fail closed, before connecting.
        var password = SecretProtector.RequireUsable(smartHost.Password, $"password of smart host {smartHost.Host}");
        return SendViaSmtpAsync(ctx, recipients, new SmtpTarget(
            smartHost.Host, smartHost.Port, smartHost.Username, password,
            ResolveTlsOption(smartHost.OpportunisticTls, smartHost.RequireTls),
            smartHost.AllowPlaintextAuth, smartHost.ConnectTimeoutSeconds,
            RequireEhloDomain(smartHost.EhloOverride, ctx.PublicHostname),
            AcceptAnyCertificate: false, AllowTlsFallback: false, ViaSmartHost: true), ct);
    }

    private async Task<List<DeliveryResult>> DeliverViaMxAsync(SendContext ctx, List<string> recipients, string domain, CancellationToken ct)
    {
        var ehloDomain = RequireEhloDomain(null, ctx.PublicHostname);
        var mxHosts = await _mxResolver.ResolveMxAsync(domain, ct);

        Exception? lastException = null;
        string? rejectedByHost = null; // the MX that rejected us at SMTP protocol level, if any
        MxTargetRefusedException? refused = null;
        var attempted = 0;
        foreach (var mxHost in mxHosts)
        {
            // M10: an MX that points at loopback, a private network or this machine is a misconfiguration or a
            // mail loop. Never connect there (a domain route is the way to deliver to an internal server).
            if (!_config.AllowPrivateMxTargets)
            {
                try
                {
                    var addresses = await _mxResolver.ResolveHostAddressesAsync(mxHost, ct);
                    var reason = ClassifyMxTarget(addresses, ctx.OwnPublicAddresses);
                    if (reason is not null)
                    {
                        refused ??= new MxTargetRefusedException(domain, mxHost, reason.Value);
                        _logger.LogWarning("Refusing MX {MxHost} for domain {Domain}: it resolves to {Reason}",
                            mxHost, domain, MxTargetRefusedException.Describe(reason.Value));
                        continue;
                    }
                }
                catch (MxLookupException ex) when (!ex.IsPermanent)
                {
                    // The host's address could not be looked up right now — a transient failure for this host.
                    lastException = ex;
                    continue;
                }
                catch (MxLookupException)
                {
                    continue; // the MX host itself does not exist — try the next one
                }
            }

            attempted++;
            try
            {
                return await SendViaSmtpAsync(ctx, recipients, new SmtpTarget(
                    mxHost, MxPort, null, null,
                    _config.OpportunisticTls ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.None,
                    AllowPlaintextAuth: false, _config.ConnectTimeoutSeconds, ehloDomain,
                    AcceptAnyCertificate: true, AllowTlsFallback: true, ViaSmartHost: false), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The SERVICE is shutting down (outer token cancelled) — not a delivery failure. Propagate so the
                // caller marks these recipients not attempted. (A per-attempt connect TIMEOUT cancels only the
                // inner linked token, so ct.IsCancellationRequested is false there and we try the next MX.)
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (ex is SmtpCommandException)
                    rejectedByHost = mxHost;
                _logger.LogWarning(ex, "Delivery to MX {MxHost} failed for domain {Domain}, trying next", mxHost, domain);
            }
        }

        // Every MX was refused by the address check and none could even be tried: permanent (a loop or a
        // private target does not fix itself by waiting).
        if (attempted == 0 && refused is not null && lastException is null)
            throw refused;

        // No MX accepted the message. Two very different failure modes must stay distinguishable in the
        // Journal: an MX that we REACHED and that REJECTED us at SMTP protocol level (its real 5xx/4xx code and
        // response text), versus no MX being reachable at all (transient 421 — a momentary outage of the
        // destination is retried, not silently bounced).
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
    /// Why an MX host's addresses must not be delivered to, or null when they are fine: loopback, unspecified,
    /// multicast and this machine's own addresses are a loop (5.4.6); private and link-local networks are not
    /// reachable destinations on the internet (5.4.4). A host with no address at all is left to the connect.
    /// </summary>
    internal static MxTargetRefusal? ClassifyMxTarget(IReadOnlyList<IPAddress> addresses, IReadOnlyCollection<IPAddress> ownPublicAddresses)
    {
        MxTargetRefusal? result = null;
        foreach (var raw in addresses)
        {
            var ip = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
                || ip.Equals(IPAddress.IPv6None) || IsMulticast(ip)
                || ownPublicAddresses.Contains(ip) || IpAddressScope.IsAssignedLocally(ip))
                return MxTargetRefusal.Loop;
            if (IpAddressScope.IsPrivateOrLoopback(ip))
                result = MxTargetRefusal.PrivateNetwork;
        }
        return result;
    }

    private static bool IsMulticast(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetworkV6
            ? ip.IsIPv6Multicast
            : ip.GetAddressBytes()[0] is >= 224 and <= 239;

    private static IReadOnlyCollection<IPAddress> OwnPublicAddresses(string? sendingIpAddresses) =>
        (sendingIpAddresses ?? "")
            .Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => IPAddress.TryParse(v, out var ip) ? ip : null)
            .OfType<IPAddress>()
            .ToHashSet();

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
    /// Maps a failure that is not attributable to a single recipient onto every recipient of the transaction.
    /// A 5xx command rejection stays permanent; a domain that does not accept mail (NXDOMAIN, null MX) and a
    /// refused MX target are permanent; everything else — connection, timeout, TLS, missing configuration, an
    /// undecryptable secret — is temporary, so it is retried within the normal window and then bounced.
    /// </summary>
    internal static List<DeliveryResult> FailureResults(IReadOnlyList<string> recipients, Exception ex, string? remoteServer, bool viaSmartHost)
    {
        var (code, text) = ex switch
        {
            SmtpCommandException sce => (((int)sce.StatusCode).ToString(), sce.Message),
            MxLookupException { IsPermanent: true } mx => ("550", $"5.1.2 {mx.Message}"),
            MxLookupException mx => ("451", $"4.4.3 {mx.Message}"),
            MxTargetRefusedException refused => ("554", refused.Message),
            SecretUnavailableException secret => ("451", $"4.7.5 {secret.Message}"),
            PlaintextAuthRefusedException plain => ("451", $"4.7.0 {plain.Message}"),
            EhloNotConfiguredException ehlo => ("451", $"4.3.5 {ehlo.Message}"),
            MailKit.Security.AuthenticationException auth => ("451", $"4.7.0 Smart host authentication failed: {auth.Message}"),
            OperationCanceledException => ("421", "4.4.2 The connection timed out"),
            _ => ("451", $"4.4.0 {ex.Message}"),
        };
        return Fail(recipients, code, text, remoteServer, viaSmartHost);
    }

    private static List<DeliveryResult> Fail(IEnumerable<string> recipients, string code, string text, string? remoteServer, bool viaSmartHost = false) =>
        recipients.Select(r => new DeliveryResult
        {
            Recipient = r,
            StatusCode = code,
            StatusMessage = text,
            RemoteServer = remoteServer,
            ViaSmartHost = viaSmartHost,
        }).ToList();

    private static IEnumerable<DeliveryResult> NotAttempted(IEnumerable<string> recipients) =>
        recipients.Select(r => new DeliveryResult
        {
            Recipient = r,
            StatusCode = "451",
            StatusMessage = "4.3.2 Not attempted: the service is shutting down",
            NotAttempted = true,
        });

    private static string DomainOf(string recipient) => recipient[(recipient.LastIndexOf('@') + 1)..];

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

    /// <summary>One SMTP destination and how to talk to it.</summary>
    /// <param name="AcceptAnyCertificate">Direct MX: opportunistic TLS encrypts against passive listeners but cannot
    /// authenticate the receiver (most MX certificates do not match the MX name), so any certificate is accepted
    /// and the validation result is recorded. Smart hosts and RequireTls validate strictly.</param>
    /// <param name="AllowTlsFallback">When STARTTLS negotiation itself fails, retry the same host once without TLS
    /// (direct MX only; never with RequireTls).</param>
    private sealed record SmtpTarget(
        string Host, int Port, string? Username, string? Password, SecureSocketOptions Tls, bool AllowPlaintextAuth,
        int ConnectTimeoutSeconds, string EhloDomain, bool AcceptAnyCertificate, bool AllowTlsFallback, bool ViaSmartHost);

    private async Task<List<DeliveryResult>> SendViaSmtpAsync(SendContext ctx, List<string> recipients, SmtpTarget target, CancellationToken ct)
    {
        try
        {
            return await SendOnceAsync(ctx, recipients, target, target.Tls, ct);
        }
        catch (SslHandshakeException ex) when (target.AllowTlsFallback && target.Tls == SecureSocketOptions.StartTlsWhenAvailable)
        {
            // STARTTLS was offered but could not be negotiated (protocol/cipher mismatch, broken TLS stack). Plain
            // SMTP is what an MX without TLS would get anyway, so try this host once more without TLS.
            _logger.LogWarning(ex, "STARTTLS negotiation with {Host}:{Port} failed — retrying once without TLS", target.Host, target.Port);
            return await SendOnceAsync(ctx, recipients, target, SecureSocketOptions.None, ct);
        }
    }

    private async Task<List<DeliveryResult>> SendOnceAsync(
        SendContext ctx, List<string> recipients, SmtpTarget target, SecureSocketOptions tlsOption, CancellationToken ct)
    {
        // PerRecipientSmtpClient records each recipient's RCPT TO verdict instead of MailKit's default of
        // aborting the whole envelope on the first rejection. Without it, a single bad address (e.g. a
        // non-existent mailbox) fails — and would auto-suppress — every valid co-recipient in the same
        // same-domain transaction.
        using var client = new PerRecipientSmtpClient(new MailKitProtocolLogger(_logger));
        client.Timeout = target.ConnectTimeoutSeconds * 1000;

        // Announce a fully qualified EHLO/HELO name. MailKit's default is the bare machine name, which
        // strict receivers reject ("550 Is neither a FQDN nor a IP literal", RFC 5321 §4.1.4).
        client.LocalDomain = target.EhloDomain;

        // Bind outbound connections to the tenant's source IP when configured.
        if (ctx.EgressEndPoint is not null)
        {
            client.LocalEndPoint = ctx.EgressEndPoint;
            _logger.LogDebug("Binding outbound delivery to source IP {EgressIp}", ctx.EgressEndPoint.Address);
        }

        var tls = new TlsObservation();
        if (target.AcceptAnyCertificate)
        {
            client.CheckCertificateRevocation = false;
            client.ServerCertificateValidationCallback = (_, _, _, errors) =>
            {
                tls.Errors = errors;
                return true;
            };
        }

        _logger.LogDebug("Connecting to {Host}:{Port} (TLS={TlsOption}, Timeout={Timeout}s)",
            target.Host, target.Port, tlsOption, target.ConnectTimeoutSeconds);

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(target.ConnectTimeoutSeconds));
            await client.ConnectAsync(target.Host, target.Port, tlsOption, connectCts.Token);
        }

        if (!string.IsNullOrWhiteSpace(target.Username))
        {
            // M13: never send credentials in clear text unless the connector explicitly allows it.
            if (!client.IsSecure && !target.AllowPlaintextAuth)
            {
                try { await client.DisconnectAsync(true, ct); } catch { /* best-effort QUIT */ }
                throw new PlaintextAuthRefusedException(target.Host, target.Port);
            }
            await client.AuthenticateAsync(target.Username, target.Password, ct);
        }

        var senderAddress = ParseEnvelopeSender(ctx.Sender);
        var recipientAddresses = recipients.Select(MailboxAddress.Parse).ToList();
        var remoteServer = $"{target.Host}:{target.Port}";
        var tlsNote = DescribeTls(client, tls, target.AcceptAnyCertificate);

        try
        {
            // MailKit still delivers the DATA payload to the accepted recipients; rejected ones are
            // captured per-recipient in the client.
            await client.SendAsync(ctx.Message, senderAddress, recipientAddresses, ct);
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
                try { await client.DisconnectAsync(true, ct); }
                catch { /* best-effort QUIT; the per-recipient verdicts are already captured */ }
            }
        }
        // Any OTHER failure from SendAsync (sender rejected, DATA rejected, dropped connection, timeout) is
        // not recipient-attributable, so it propagates here and the caller tries the next MX as before.

        var results = BuildRecipientResults(recipients, recipientAddresses, client.Rejected, remoteServer, tlsNote, target.ViaSmartHost);

        var accepted = results.Where(r => r.Success).Select(r => r.Recipient).ToList();
        if (accepted.Count > 0)
            _logger.LogInformation("Delivered to {Recipients} via {Host}:{Port} ({Tls})",
                string.Join(", ", accepted), target.Host, target.Port, tlsNote);
        foreach (var r in results.Where(r => !r.Success))
            _logger.LogWarning("Recipient {Recipient} rejected by {Host}:{Port}: {Code} {Message}",
                r.Recipient, target.Host, target.Port, r.StatusCode, r.StatusMessage);

        return results;
    }

    /// <summary>
    /// The envelope sender for MAIL FROM. An empty stored sender is the null return path (<c>MAIL FROM:&lt;&gt;</c>)
    /// of a bounce/DSN — MailKit sends an empty addr-spec for a mailbox with an empty address.
    /// </summary>
    internal static MailboxAddress ParseEnvelopeSender(string sender) =>
        string.IsNullOrWhiteSpace(sender) || sender.Trim() == "<>"
            ? new MailboxAddress(string.Empty, string.Empty)
            : MailboxAddress.Parse(sender);

    /// <summary>What the TLS state of a connection was, for the delivery log: "TLS 1.3, certificate not verified: …".</summary>
    private static string DescribeTls(SmtpClient client, TlsObservation tls, bool acceptAnyCertificate)
    {
        if (!client.IsSecure)
            return "no TLS";
        var protocol = client.SslProtocol switch
        {
            System.Security.Authentication.SslProtocols.Tls13 => "TLS 1.3",
            System.Security.Authentication.SslProtocols.Tls12 => "TLS 1.2",
            var p => p.ToString(),
        };
        if (!acceptAnyCertificate)
            return $"{protocol}, certificate verified";
        return tls.Errors == SslPolicyErrors.None
            ? $"{protocol}, certificate verified"
            : $"{protocol}, certificate not verified: {tls.Errors}";
    }

    private sealed class TlsObservation
    {
        public SslPolicyErrors Errors { get; set; }
    }

    /// <summary>
    /// Maps the recorded per-recipient RCPT TO verdicts onto <see cref="DeliveryResult"/>s. A recipient
    /// found in <paramref name="rejected"/> carries its real status code and is flagged
    /// <see cref="DeliveryResult.RecipientRejected"/>; every other recipient was accepted and received the
    /// DATA payload (250).
    /// </summary>
    private static List<DeliveryResult> BuildRecipientResults(
        List<string> recipients, List<MailboxAddress> recipientAddresses,
        IReadOnlyDictionary<string, SmtpResponse> rejected, string remoteServer, string tlsNote, bool viaSmartHost)
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
                    RecipientRejected = true,
                    ViaSmartHost = viaSmartHost,
                });
            }
            else
            {
                results.Add(new DeliveryResult
                {
                    Recipient = recipients[i],
                    StatusCode = "250",
                    StatusMessage = $"Delivered via {remoteServer} ({tlsNote})",
                    RemoteServer = remoteServer,
                    ViaSmartHost = viaSmartHost,
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
/// Exception that carries per-recipient delivery results even when some recipients fail. The delivery service
/// itself returns its results instead of throwing; the worker still accepts this from other implementations.
/// </summary>
public class DeliveryException(string message, IReadOnlyList<DeliveryResult> results)
    : Exception(message)
{
    public IReadOnlyList<DeliveryResult> Results { get; } = results;
}

/// <summary>
/// No fully qualified EHLO name can be resolved (no connector override, no public hostname configured, and the
/// machine has no DNS suffix). Counted as a temporary failure (4.3.5): retried within the normal window, then
/// bounced — a configuration gap must not hold mail for ever. The daily self-check reports the same condition.
/// </summary>
public sealed class EhloNotConfiguredException() : Exception(
    "Outbound delivery is paused: no fully qualified EHLO hostname is available. Set the public hostname "
    + "under Settings → Sending identity (ideally the FQDN matching the PTR of the outbound IP), or an "
    + "EHLO override on the send connector. Announcing this machine's unqualified name would get mail "
    + "rejected by strict receivers (RFC 5321).");

/// <summary>A smart host that requires credentials did not offer TLS, and plaintext AUTH is not allowed (M13).</summary>
public sealed class PlaintextAuthRefusedException(string host, int port) : Exception(
    $"Refusing to send the credentials for smart host {host}:{port} over an unencrypted connection: the server did "
    + "not offer STARTTLS. Enable TLS on the smart host, or allow plaintext authentication on the send connector.");

/// <summary>Why an MX target was refused.</summary>
public enum MxTargetRefusal
{
    /// <summary>Loopback, unspecified, multicast or this machine's own address — delivering there loops.</summary>
    Loop,

    /// <summary>A private or link-local network address — not a destination on the internet.</summary>
    PrivateNetwork,
}

/// <summary>Every MX of a domain points at an address the relay must not deliver to (M10). Permanent.</summary>
public sealed class MxTargetRefusedException(string domain, string mxHost, MxTargetRefusal reason) : Exception(
    (reason == MxTargetRefusal.Loop ? "5.4.6" : "5.4.4")
    + $" The MX of {domain} ({mxHost}) resolves to {Describe(reason)}; refusing to deliver there. Add a domain route "
    + "(send connector) if this destination is intended.")
{
    public MxTargetRefusal Reason { get; } = reason;

    internal static string Describe(MxTargetRefusal reason) => reason == MxTargetRefusal.Loop
        ? "a loopback or this relay's own address (mail loop)"
        : "a private network address";
}

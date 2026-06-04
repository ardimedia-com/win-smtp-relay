using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
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
    private readonly ILogger<SmtpDeliveryService> _logger;

    public SmtpDeliveryService(
        IMxResolver mxResolver,
        IOptions<DeliveryOptions> options,
        IRuntimeConfigCache configCache,
        DkimSigningService dkimSigner,
        IDkimDomainService dkimDomains,
        ILogger<SmtpDeliveryService> logger)
    {
        _mxResolver = mxResolver;
        _config = options.Value;
        _configCache = configCache;
        _dkimSigner = dkimSigner;
        _dkimDomains = dkimDomains;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DeliveryResult>> DeliverAsync(QueuedMessage message, CancellationToken cancellationToken = default)
    {
        var mimeMessage = await MimeMessage.LoadAsync(new MemoryStream(message.RawMessage), cancellationToken);

        // DKIM-sign before sending, scoped to the message's tenant so a tenant can only sign with
        // its own key (no-op if the tenant has no DKIM key for the sender domain).
        var senderDomain = mimeMessage.From.Mailboxes.FirstOrDefault()?.Domain;
        var tenantDkim = senderDomain is null
            ? null
            : await _dkimDomains.GetForSigningAsync(message.TenantId, senderDomain, cancellationToken);
        _dkimSigner.Sign(mimeMessage, message.TenantId, tenantDkim);

        var recipients = message.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<DeliveryResult>();

        // Group recipients by domain for efficient delivery
        var byDomain = recipients.GroupBy(r => r.Split('@').Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var domainGroup in byDomain)
        {
            var domain = domainGroup.Key;
            var domainRecipients = domainGroup.ToList();

            var domainResults = await DeliverToDomainAsync(mimeMessage, message.Sender, domainRecipients, domain, message.TenantId, cancellationToken);
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
                connector.OpportunisticTls,
                connector.ConnectTimeoutSeconds,
                cancellationToken);
        }

        // 2. Global smart host
        if (!string.IsNullOrWhiteSpace(_config.SmartHost))
        {
            return await SendViaSmtpAsync(
                mimeMessage, sender, recipients,
                _config.SmartHost, _config.SmartHostPort,
                _config.SmartHostUsername, _config.SmartHostPassword,
                _config.OpportunisticTls,
                _config.ConnectTimeoutSeconds,
                cancellationToken);
        }

        // 3. Direct MX delivery
        var mxHosts = await _mxResolver.ResolveMxAsync(domain, cancellationToken);

        Exception? lastException = null;
        foreach (var mxHost in mxHosts)
        {
            try
            {
                return await SendViaSmtpAsync(mimeMessage, sender, recipients, mxHost, 25, null, null,
                    _config.OpportunisticTls, _config.ConnectTimeoutSeconds, cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "Delivery to MX {MxHost} failed for domain {Domain}, trying next",
                    mxHost, domain);
            }
        }

        // All MX hosts exhausted — return failure results for all recipients
        var errorMessage = lastException?.Message ?? "All MX hosts exhausted";
        return recipients.Select(r => new DeliveryResult
        {
            Recipient = r,
            StatusCode = "550",
            StatusMessage = $"All MX hosts exhausted for domain {domain}: {errorMessage}",
            RemoteServer = mxHosts.FirstOrDefault()
        }).ToList();
    }

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
        int connectTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(new MailKitProtocolLogger(_logger));
        client.Timeout = connectTimeoutSeconds * 1000;

        var tlsOption = opportunisticTls
            ? SecureSocketOptions.StartTlsWhenAvailable
            : SecureSocketOptions.None;

        _logger.LogDebug("Connecting to {Host}:{Port} (TLS={TlsOption}, Timeout={Timeout}s)",
            host, port, tlsOption, connectTimeoutSeconds);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSeconds));

        await client.ConnectAsync(host, port, tlsOption, connectCts.Token);

        if (!string.IsNullOrWhiteSpace(username))
            await client.AuthenticateAsync(username, password, cancellationToken);

        var senderAddress = MailboxAddress.Parse(sender);
        var recipientAddresses = recipients.Select(MailboxAddress.Parse).ToList();

        await client.SendAsync(mimeMessage, senderAddress, recipientAddresses, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        var remoteServer = $"{host}:{port}";
        _logger.LogInformation("Delivered to {Recipients} via {Host}:{Port}",
            string.Join(", ", recipients), host, port);

        return recipients.Select(r => new DeliveryResult
        {
            Recipient = r,
            StatusCode = "250",
            StatusMessage = $"Delivered via {remoteServer}",
            RemoteServer = remoteServer
        }).ToList();
    }
}

/// <summary>
/// Exception that carries per-recipient delivery results even when some recipients fail.
/// </summary>
public class DeliveryException(string message, IReadOnlyList<DeliveryResult> results)
    : Exception(message)
{
    public IReadOnlyList<DeliveryResult> Results { get; } = results;
}

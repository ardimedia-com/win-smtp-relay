using System.Buffers;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;
using WinSmtpRelay.Security.Models;

namespace WinSmtpRelay.SmtpListener;

public class RelayMessageStore : MessageStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EmailAuthenticationService _emailAuth;
    private readonly WebhookService _webhookService;
    private readonly IActivityNotifier _activityNotifier;
    private readonly ILogger<RelayMessageStore> _logger;

    public RelayMessageStore(
        IServiceScopeFactory scopeFactory,
        EmailAuthenticationService emailAuth,
        WebhookService webhookService,
        IActivityNotifier activityNotifier,
        ILogger<RelayMessageStore> logger)
    {
        _scopeFactory = scopeFactory;
        _emailAuth = emailAuth;
        _webhookService = webhookService;
        _activityNotifier = activityNotifier;
        _logger = logger;
    }

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        var rawMessage = buffer.ToArray();
        var sender = transaction.From.AsAddress();
        var recipients = string.Join(";", transaction.To.Select(m => m.AsAddress()));
        var messageId = ExtractMessageId(rawMessage) ?? $"<{Guid.NewGuid()}@winsmtprelay>";

        var tenantId = context.Properties.TryGetValue("TenantId", out var tenantObj) && tenantObj is int resolvedTenant
            ? resolvedTenant
            : TenantDefaults.DefaultTenantId;

        var remoteEndPoint = context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            ? ep as IPEndPoint
            : null;
        var sourceIp = remoteEndPoint?.Address.ToString();

        // Get envelope From domain (stored by RelayMailboxFilter, or extract here)
        var envelopeFromDomain = context.Properties.TryGetValue("EnvelopeFromDomain", out var domObj)
            ? domObj as string ?? GetDomainFromAddress(sender)
            : GetDomainFromAddress(sender);

        // Run full authentication check (DMARC needs the RFC5322.From header domain)
        var headerFromDomain = ExtractFromDomain(rawMessage) ?? envelopeFromDomain;
        var authResults = await _emailAuth.CheckAllAsync(
            remoteEndPoint?.Address ?? IPAddress.Loopback,
            envelopeFromDomain,
            headerFromDomain,
            cancellationToken);

        // Enforce DMARC/SPF policy
        if (await _emailAuth.ShouldRejectAsync(authResults, cancellationToken))
        {
            _logger.LogWarning("Message {MessageId} rejected: DMARC/SPF policy failure (from {Sender})",
                messageId, sender);
            return new SmtpResponse(SmtpReplyCode.MailboxUnavailable,
                "5.7.1 Message rejected due to DMARC/SPF policy");
        }

        // Add Authentication-Results header to message
        rawMessage = PrependAuthenticationResultsHeader(rawMessage, authResults);

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
            TenantId = tenantId,
            NextRetryUtc = DateTime.UtcNow
        };

        var id = await queue.EnqueueAsync(message, cancellationToken);

        _logger.LogInformation(
            "Message {MessageId} queued (id={QueueId}) from {Sender} to {Recipients} ({Size} bytes) via {SourceIp} spf={SpfVerdict}",
            messageId, id, sender, recipients, rawMessage.Length, sourceIp ?? "unknown",
            authResults.Spf.Verdict);

        // Fire notifications (fire-and-forget, do not block SMTP response)
        _ = _webhookService.NotifyMessageReceivedAsync(messageId, sender, recipients, rawMessage.Length, sourceIp, CancellationToken.None);
        _ = _activityNotifier.NotifyMessageReceivedAsync(messageId, sender, recipients, rawMessage.Length);

        return SmtpResponse.Ok;
    }

    private static byte[] PrependAuthenticationResultsHeader(byte[] rawMessage, AuthenticationResults results)
    {
        if (results.Spf.Verdict == SpfVerdict.None && results.Dmarc.Verdict == DmarcVerdict.None)
            return rawMessage;

        var headerValue = results.ToHeaderValue("winsmtprelay");
        var header = $"Authentication-Results: {headerValue}\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        var combined = new byte[headerBytes.Length + rawMessage.Length];
        Buffer.BlockCopy(headerBytes, 0, combined, 0, headerBytes.Length);
        Buffer.BlockCopy(rawMessage, 0, combined, headerBytes.Length, rawMessage.Length);
        return combined;
    }

    private static string? ExtractMessageId(byte[] rawMessage)
    {
        var text = Encoding.ASCII.GetString(rawMessage, 0, Math.Min(rawMessage.Length, 4096));
        var idx = text.IndexOf("Message-ID:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            idx = text.IndexOf("Message-Id:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var start = text.IndexOf('<', idx);
        var end = text.IndexOf('>', start + 1);
        if (start >= 0 && end > start)
            return text[start..(end + 1)];

        return null;
    }

    private static string? ExtractFromDomain(byte[] rawMessage)
    {
        var text = Encoding.ASCII.GetString(rawMessage, 0, Math.Min(rawMessage.Length, 4096));

        // Find "From:" header (not at start of a Message-ID or X-header line)
        var idx = text.IndexOf("\r\nFrom:", StringComparison.OrdinalIgnoreCase);
        int lineStart;
        if (idx >= 0)
        {
            lineStart = idx + 2; // skip \r\n
        }
        else if (text.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
        {
            lineStart = 0;
        }
        else
        {
            return null;
        }

        var lineEnd = text.IndexOf('\n', lineStart + 5);
        if (lineEnd < 0) lineEnd = text.Length;

        var fromLine = text[lineStart..lineEnd];
        var atIdx = fromLine.LastIndexOf('@');
        if (atIdx < 0) return null;

        var domainStart = atIdx + 1;
        var domainEnd = domainStart;
        while (domainEnd < fromLine.Length &&
               fromLine[domainEnd] != '>' && fromLine[domainEnd] != ' ' &&
               fromLine[domainEnd] != '\r' && fromLine[domainEnd] != '\n' &&
               fromLine[domainEnd] != ';')
            domainEnd++;

        return domainEnd > domainStart ? fromLine[domainStart..domainEnd] : null;
    }

    private static string GetDomainFromAddress(string emailAddress)
    {
        var atIndex = emailAddress.LastIndexOf('@');
        return atIndex >= 0 ? emailAddress[(atIndex + 1)..] : emailAddress;
    }
}

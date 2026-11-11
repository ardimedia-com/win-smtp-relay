using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Delivery.Dsn;

/// <inheritdoc cref="IDsnService"/>
public sealed class DsnService(
    RelayDbContext db,
    IMessageQueue queue,
    ISuppressionService suppression,
    IDnsSettingsService dnsSettings,
    ILogger<DsnService> logger) : IDsnService
{
    public async Task<bool> NotifyAsync(QueuedMessage message, IReadOnlyList<DsnRecipient> recipients, CancellationToken ct = default)
    {
        if (recipients.Count == 0)
            return false;

        // Never for system mail or another DSN, never for null-sender mail (that is how bounce loops start).
        if (message.Origin != MessageOrigin.Submitted)
            return false;
        var sender = message.Sender.Trim();
        if (sender.Length == 0 || sender == "<>" || sender.IndexOf('@') <= 0)
            return false;
        if (message.RawMessage.Length > 0 && DsnComposer.IsDeliveryReport(message.RawMessage))
            return false;

        // Explicit tenant filters + IgnoreQueryFilters: the worker runs unscoped, and the check must be about
        // the MESSAGE's tenant, not an ambient one.
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == message.TenantId, ct);
        if (tenant is not { DsnEnabled: true })
            return false;

        // Backscatter guard: only answer a sender in one of this tenant's own domains. An envelope sender can be
        // forged by anyone who can submit; a DSN to a domain the tenant does not own would mail a stranger.
        var senderDomain = sender[(sender.LastIndexOf('@') + 1)..];
        if (!await IsTenantDomainAsync(message.TenantId, senderDomain, ct))
        {
            logger.LogInformation(
                "No DSN for message {MessageId}: sender domain {Domain} is not a domain of tenant {TenantId}",
                message.MessageId, senderDomain, message.TenantId);
            return false;
        }

        if (await suppression.IsSuppressedAsync(sender, message.TenantId, ct))
        {
            logger.LogInformation("No DSN for message {MessageId}: the sender is on the suppression list", message.MessageId);
            return false;
        }

        recipients = await WithLastReplyAsync(message.Id, recipients, ct);
        var settings = await dnsSettings.GetAsync(ct);
        var reportingMta = EhloHostname.Resolve(null, settings.PublicHostname) ?? Environment.MachineName;
        var raw = DsnComposer.ComposeBytes(new DsnInput(
            reportingMta, sender, message.MessageId, message.CreatedUtc, message.RawMessage, recipients, DateTimeOffset.UtcNow));

        await queue.EnqueueAsync(new QueuedMessage
        {
            TenantId = message.TenantId,
            MessageId = $"<dsn-{Guid.NewGuid():N}@{reportingMta}>",
            Sender = "", // null return path: a DSN is never answered with another DSN (RFC 3461 §6.2)
            Recipients = sender,
            RawMessage = raw,
            SizeBytes = raw.Length,
            Origin = MessageOrigin.DeliveryStatusNotification,
            NextRetryUtc = DateTimeOffset.UtcNow,
        }, ct);

        logger.LogInformation("DSN queued to {Sender} for message {MessageId}: {Count} undelivered recipient(s)",
            sender, message.MessageId, recipients.Count);
        return true;
    }

    /// <summary>
    /// A recipient that failed on an EARLIER attempt (permanent failures are final and not retried) has no reply in
    /// the current attempt; its last reply is in the delivery log.
    /// </summary>
    private async Task<IReadOnlyList<DsnRecipient>> WithLastReplyAsync(long messageId, IReadOnlyList<DsnRecipient> recipients, CancellationToken ct)
    {
        var unknown = recipients.Where(r => r.StatusCode is null && !r.Suppressed).Select(r => r.Address).ToList();
        if (unknown.Count == 0)
            return recipients;

        // Ordered by Id (chronological by construction) — no ORDER BY on the DateTimeOffset column.
        var rows = await db.DeliveryLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.QueuedMessageId == messageId && unknown.Contains(l.Recipient) && !l.StatusCode.StartsWith("2"))
            .OrderByDescending(l => l.Id)
            .Select(l => new { l.Recipient, l.StatusCode, l.StatusMessage, l.RemoteServer })
            .ToListAsync(ct);
        var last = rows.GroupBy(l => l.Recipient, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return [.. recipients.Select(r => r.StatusCode is null && !r.Suppressed && last.TryGetValue(r.Address, out var l)
            ? r with { StatusCode = l.StatusCode, StatusMessage = l.StatusMessage, RemoteServer = l.RemoteServer }
            : r)];
    }

    private async Task<bool> IsTenantDomainAsync(int tenantId, string domain, CancellationToken ct)
    {
        var accepted = await db.AcceptedDomains.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId).Select(d => d.Domain).ToListAsync(ct);
        var senders = await db.AcceptedSenderDomains.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId).Select(d => d.Domain).ToListAsync(ct);
        return accepted.Concat(senders).Any(d => string.Equals(d.Trim().TrimEnd('.'), domain, StringComparison.OrdinalIgnoreCase));
    }
}

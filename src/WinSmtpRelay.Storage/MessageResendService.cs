using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

/// <summary>
/// EF-backed <see cref="IMessageResendService"/>. The body is read ONLY through <see cref="IMessageBodyAccess"/>
/// (purpose "resend"), so a resend is subject to the same consent rule — a tenant-admin membership in the message's
/// tenant — and leaves the same audit row as any other read of customer mail content.
/// </summary>
public class MessageResendService(RelayDbContext db, IMessageBodyAccess bodies) : IMessageResendService
{
    public const string BodyPurpose = "resend";

    public async Task<ResendOutcome> ResendAsync(long sourceMessageId, IReadOnlyList<string> recipients, CancellationToken ct = default)
    {
        var clean = recipients
            .Select(r => r?.Trim() ?? "")
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (clean.Count == 0)
            return ResendOutcome.Failed("Enter at least one recipient address.");
        var invalid = clean.FirstOrDefault(r => !IsPlausibleAddress(r));
        if (invalid is not null)
            return ResendOutcome.Failed($"\"{invalid}\" is not a valid email address.");

        // Metadata only — the tenant query filter applies, so a tenant can only resend its own messages.
        var source = await db.QueuedMessages.AsNoTracking()
            .Where(m => m.Id == sourceMessageId)
            .Select(m => new { m.TenantId, m.MessageId, m.Sender, m.SizeBytes, m.SourceIp, m.AuthenticatedUser, m.Origin })
            .FirstOrDefaultAsync(ct);
        if (source is null)
            return ResendOutcome.Failed(NotFound);

        // The recipients were validated above, before this read, so a typo does not leave a body-read audit row behind.
        var read = await bodies.ReadAsync(sourceMessageId, BodyPurpose, ct);
        switch (read.Status)
        {
            case MessageBodyReadStatus.NotFound:
                return ResendOutcome.Failed(NotFound);
            case MessageBodyReadStatus.Forbidden:
                return ResendOutcome.Failed(
                    "Resending reads the message content, which needs a tenant-admin membership in the message's tenant. "
                    + "A host administrator can take one (break-glass, audited) on the tenant's admin page.");
            case MessageBodyReadStatus.NoBody:
                return ResendOutcome.Failed(
                    "The message body is no longer retained, so it cannot be resent. Increase the resend-retention "
                    + "window under Settings → Data retention to keep undelivered bodies longer.");
        }

        // A fresh queue entry that reuses the stored body/sender. The delivery worker re-evaluates
        // suppression, runs the message filters, and tracks DeliveredRecipients independently.
        var resend = new QueuedMessage
        {
            TenantId = source.TenantId,
            MessageId = source.MessageId,
            Sender = source.Sender,
            Recipients = string.Join(';', clean),
            RawMessage = read.Body!,
            SizeBytes = source.SizeBytes,
            Status = MessageStatus.Queued,
            CreatedUtc = DateTimeOffset.UtcNow,
            SourceIp = source.SourceIp,
            AuthenticatedUser = source.AuthenticatedUser,
            Origin = source.Origin,
            NextRetryUtc = DateTimeOffset.UtcNow
        };
        db.QueuedMessages.Add(resend);
        await db.SaveChangesAsync(ct);
        return ResendOutcome.Ok(resend.Id);
    }

    private const string NotFound = "Message not found.";

    /// <summary>Cheap sanity check (a single "@", a "." in the domain). The delivery path does the real parse.</summary>
    private static bool IsPlausibleAddress(string address)
    {
        var at = address.IndexOf('@');
        return at > 0
            && at < address.Length - 1
            && address.IndexOf('@', at + 1) < 0
            && address.LastIndexOf('.') > at;
    }
}

using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class MessageResendService(RelayDbContext db) : IMessageResendService
{
    public async Task<ResendOutcome> ResendAsync(long sourceMessageId, IReadOnlyList<string> recipients, CancellationToken ct = default)
    {
        // The tenant query filter applies, so a tenant can only resend its own messages.
        var source = await db.QueuedMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == sourceMessageId, ct);
        if (source is null)
            return ResendOutcome.Failed("Message not found.");
        if (source.RawMessage is null || source.RawMessage.Length == 0)
            return ResendOutcome.Failed(
                "The message body is no longer retained, so it cannot be resent. Increase the resend-retention "
                + "window under Settings → Data retention to keep undelivered bodies longer.");

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

        // A fresh queue entry that reuses the stored body/sender. The delivery worker re-evaluates
        // suppression, runs the message filters, and tracks DeliveredRecipients independently.
        var resend = new QueuedMessage
        {
            TenantId = source.TenantId,
            MessageId = source.MessageId,
            Sender = source.Sender,
            Recipients = string.Join(';', clean),
            RawMessage = source.RawMessage,
            SizeBytes = source.SizeBytes,
            Status = MessageStatus.Queued,
            CreatedUtc = DateTimeOffset.UtcNow,
            SourceIp = source.SourceIp,
            AuthenticatedUser = source.AuthenticatedUser
        };
        db.QueuedMessages.Add(resend);
        await db.SaveChangesAsync(ct);
        return ResendOutcome.Ok(resend.Id);
    }

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

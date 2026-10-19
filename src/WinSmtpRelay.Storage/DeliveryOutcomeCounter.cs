using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Per-recipient delivery outcomes of a period (host-wide), for the daily digest, the bounce-rate alert and the
/// self-check: <see cref="DeliveryOutcomes.Classify"/> over the delivery log, with the current status of the
/// messages whose attempts failed.
/// </summary>
public static class DeliveryOutcomeCounter
{
    public static async Task<DeliveryOutcomeCounts> CountAsync(RelayDbContext db, DateTimeOffset sinceUtc, CancellationToken ct)
    {
        var attempts = await db.DeliveryLogs.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.TimestampUtc >= sinceUtc)
            .Select(l => new DeliveryAttempt(l.QueuedMessageId, l.Recipient, l.StatusCode, l.StatusMessage, l.TimestampUtc))
            .ToListAsync(ct);

        var failedMessageIds = attempts
            .Where(a => !a.StatusCode.StartsWith('2'))
            .Select(a => a.QueuedMessageId)
            .Distinct()
            .ToList();

        var statuses = failedMessageIds.Count == 0
            ? new Dictionary<long, MessageStatus>()
            : await db.QueuedMessages.AsNoTracking().IgnoreQueryFilters()
                .Where(m => failedMessageIds.Contains(m.Id))
                .Select(m => new { m.Id, m.Status })
                .ToDictionaryAsync(m => m.Id, m => m.Status, ct);

        return DeliveryOutcomes.Classify(attempts, statuses);
    }
}

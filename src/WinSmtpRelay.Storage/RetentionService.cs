using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Applies the host data-retention policy. Resolve and run this from a plain background scope (no tenant
/// set) so the tenant query filter is disabled and the purge spans every tenant — see <c>CurrentTenant</c>.
/// </summary>
public class RetentionService(
    RelayDbContext db,
    IDataRetentionSettingsService settingsService,
    ILogger<RetentionService> logger) : IRetentionService
{
    public async Task<RetentionPurgeResult> RunPurgeAsync(CancellationToken ct = default)
    {
        var s = await settingsService.GetAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // Message history: terminal messages (delivered/bounced/failed/suppressed) older than the window;
        // in-flight rows (Queued/Delivering) are never purged regardless of age. The cutoff is on the
        // non-nullable CreatedUtc so SQLite's ISO-string DateTimeOffset converter can range-filter it (a
        // comparison on the nullable CompletedUtc cannot be translated — see RelayDbContext's converter note).
        var messageCutoff = now.AddDays(-Math.Max(1, s.MessageHistoryDays));
        var messages = await db.QueuedMessages
            .Where(m => (m.Status == MessageStatus.Delivered
                      || m.Status == MessageStatus.Bounced
                      || m.Status == MessageStatus.Failed
                      || m.Status == MessageStatus.Suppressed)
                     && m.CreatedUtc < messageCutoff)
            .ExecuteDeleteAsync(ct);

        // Delivery audit log: re-apply the floor here too (defence in depth alongside the settings service).
        var logCutoff = now.AddDays(-Math.Max(DataRetentionSettings.DeliveryLogFloorDays, s.DeliveryLogDays));
        var deliveryLogs = await db.DeliveryLogs
            .Where(l => l.TimestampUtc < logCutoff)
            .ExecuteDeleteAsync(ct);

        // Refused submissions: the reject-side counterpart of the delivery log, so they observe the same
        // observability window (DeliveryLogDays) rather than carrying a setting of their own.
        // Aged out by LastSeenUtc, NOT FirstSeenUtc: a device that has been failing since March is a
        // current problem, not an old record, and purging it by its start date would delete exactly the
        // long-running misconfigurations this table exists to surface. (The row count is separately
        // capped per trust partition by the recorder; this is the age-based half.)
        var rejectedSubmissions = await db.RejectedSubmissions
            .Where(r => r.LastSeenUtc < logCutoff)
            .ExecuteDeleteAsync(ct);

        // Suppression list: entries from hard bounces expire after SuppressionDays (default 30, T1075) — a mailbox
        // that did not exist may exist again. Complaints and manual entries are deliberate and never age out.
        // 0 = keep hard bounces indefinitely.
        var suppressions = 0;
        if (s.SuppressionDays > 0)
        {
            var suppressionCutoff = now.AddDays(-s.SuppressionDays);
            suppressions = await db.SuppressionEntries
                .Where(e => e.Reason == SuppressionReason.HardBounce && e.CreatedUtc < suppressionCutoff)
                .ExecuteDeleteAsync(ct);
        }

        // Resend grace: a partially-delivered message (one with undelivered recipients, e.g. a recipient
        // skipped by the suppression list) keeps its body so an admin can resend it. Once it ages past the
        // resend window the body is dropped — the metadata row stays until MessageHistoryDays. Only when
        // bodies are stripped on delivery (minimisation on); archive/regulated profiles
        // (StripBodyOnDelivery off) deliberately retain content for the whole history window.
        var resendBodiesStripped = 0;
        if (s.StripBodyOnDelivery && s.ResendRetentionDays > 0)
        {
            var resendCutoff = now.AddDays(-s.ResendRetentionDays);
            // Candidates: terminal messages past the window that still carry a body. Fully-delivered
            // messages already had their body stripped at delivery, so a present body here means the
            // message has undelivered recipients — but confirm in memory (set difference isn't translatable).
            // Note: an empty-blob inequality (`RawMessage <> x''`) is used rather than `RawMessage.Length > 0`
            // because EF rewrites the latter to a non-translatable `Any()` over the binary column.
            var emptyBody = Array.Empty<byte>();
            var withBody = await db.QueuedMessages
                .Where(m => (m.Status == MessageStatus.Delivered
                          || m.Status == MessageStatus.Bounced
                          || m.Status == MessageStatus.Failed
                          || m.Status == MessageStatus.Suppressed)
                         && m.CreatedUtc < resendCutoff
                         && m.RawMessage != emptyBody)
                .Select(m => new { m.Id, m.Recipients, m.DeliveredRecipients })
                .ToListAsync(ct);

            var staleIds = withBody
                .Where(m => RecipientList.HasUndelivered(m.Recipients, m.DeliveredRecipients))
                .Select(m => m.Id)
                .ToList();

            if (staleIds.Count > 0)
                resendBodiesStripped = await db.QueuedMessages
                    .Where(m => staleIds.Contains(m.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(m => m.RawMessage, Array.Empty<byte>()), ct);
        }

        if (messages + deliveryLogs + suppressions + resendBodiesStripped + rejectedSubmissions > 0)
            logger.LogInformation(
                "Retention purge removed {Messages} message(s), {Logs} delivery-log row(s), {Suppressions} suppression(s), "
                + "{Rejections} rejected-submission row(s), stripped {ResendBodies} resend-grace body/bodies",
                messages, deliveryLogs, suppressions, rejectedSubmissions, resendBodiesStripped);

        await CompactIfFragmentedAsync(ct);

        return new RetentionPurgeResult(messages, deliveryLogs, suppressions, resendBodiesStripped, rejectedSubmissions);
    }

    /// <summary>Free pages that justify a VACUUM: at least this share of the file ...</summary>
    internal const double CompactFreeShare = 0.25;

    /// <summary>... and at least this many bytes.</summary>
    internal const long CompactFreeBytes = 16L * 1024 * 1024;

    /// <summary>
    /// SQLite never gives deleted pages back to the file system: after the purge removed message bodies the file
    /// keeps its size. When the free pages are a large share of the file, rebuild it (VACUUM). Runs in the nightly
    /// maintenance window; VACUUM locks the database while it runs, so it is skipped when there is little to gain.
    /// Best-effort: a failure is logged and the purge result stands.
    /// </summary>
    public async Task<bool> CompactIfFragmentedAsync(CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return false;
        try
        {
            var pageSize = await ScalarAsync("SELECT page_size AS \"Value\" FROM pragma_page_size", ct);
            var pageCount = await ScalarAsync("SELECT page_count AS \"Value\" FROM pragma_page_count", ct);
            var freePages = await ScalarAsync("SELECT freelist_count AS \"Value\" FROM pragma_freelist_count", ct);
            if (pageCount <= 0 || freePages < pageCount * CompactFreeShare || freePages * pageSize < CompactFreeBytes)
                return false;

            var started = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlRawAsync("VACUUM;", ct);
            logger.LogInformation(
                "Database compacted: {FreeMb:F1} MB of free pages released ({FreePages} of {Pages} pages) in {Seconds:F1}s",
                freePages * pageSize / 1048576.0, freePages, pageCount, (DateTimeOffset.UtcNow - started).TotalSeconds);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Database compaction (VACUUM) failed");
            return false;
        }
    }

    private async Task<long> ScalarAsync(string sql, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<long>(sql).ToListAsync(ct)).Single();

}

using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class MessageFilterService(RelayDbContext db) : IMessageFilterService
{
    // Header rewrites

    public async Task<IReadOnlyList<HeaderRewriteEntry>> GetHeaderRulesAsync(CancellationToken ct = default)
    {
        return await db.HeaderRewriteEntries.AsNoTracking().OrderBy(r => r.SortOrder).ToListAsync(ct);
    }

    public async Task<HeaderRewriteEntry> CreateHeaderRuleAsync(HeaderRewriteEntry rule, CancellationToken ct = default)
    {
        db.HeaderRewriteEntries.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task UpdateHeaderRuleAsync(HeaderRewriteEntry rule, CancellationToken ct = default)
    {
        var existing = await db.HeaderRewriteEntries.FirstOrDefaultAsync(r => r.Id == rule.Id, ct);
        if (existing is null) return;

        existing.HeaderName = rule.HeaderName;
        existing.MatchValue = rule.MatchValue;
        existing.Action = rule.Action;
        existing.NewValue = rule.NewValue;
        existing.SortOrder = rule.SortOrder;
        existing.IsEnabled = rule.IsEnabled;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteHeaderRuleAsync(int id, CancellationToken ct = default)
    {
        await db.HeaderRewriteEntries.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    // Sender rewrites

    public async Task<IReadOnlyList<SenderRewriteEntry>> GetSenderRulesAsync(CancellationToken ct = default)
    {
        return await db.SenderRewriteEntries.AsNoTracking().OrderBy(r => r.SortOrder).ToListAsync(ct);
    }

    public async Task<SenderRewriteEntry> CreateSenderRuleAsync(SenderRewriteEntry rule, CancellationToken ct = default)
    {
        db.SenderRewriteEntries.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task UpdateSenderRuleAsync(SenderRewriteEntry rule, CancellationToken ct = default)
    {
        var existing = await db.SenderRewriteEntries.FirstOrDefaultAsync(r => r.Id == rule.Id, ct);
        if (existing is null) return;

        existing.FromPattern = rule.FromPattern;
        existing.ToAddress = rule.ToAddress;
        existing.SortOrder = rule.SortOrder;
        existing.IsEnabled = rule.IsEnabled;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSenderRuleAsync(int id, CancellationToken ct = default)
    {
        await db.SenderRewriteEntries.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }
}

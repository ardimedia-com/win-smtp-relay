using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// The cache serves this data on the SMTP hot path; invalidating HERE (not in each caller) means no
// caller — UI page, API endpoint, or background job — can forget to and leave stale policy live for
// up to the cache lifetime. Same convention as IpAccessRuleService.
// Rewrite rules silently alter mail in transit — audited at the SERVICE so no caller can plant or
// remove a rewrite without leaving a trace.
public class MessageFilterService(
    RelayDbContext db,
    IRuntimeConfigCache cache,
    ICurrentActor actor,
    IAdminAuditService audit) : IMessageFilterService
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
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.HeaderRuleCreated, actor, tenantId: rule.TenantId,
            detail: $"{rule.Action} {rule.HeaderName}", ct: ct);
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
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.HeaderRuleUpdated, actor, tenantId: existing.TenantId,
            detail: $"{existing.Action} {existing.HeaderName} enabled={existing.IsEnabled}", ct: ct);
    }

    public async Task DeleteHeaderRuleAsync(int id, CancellationToken ct = default)
    {
        // Load-then-delete so the audit row can name the rule that was removed.
        var existing = await db.HeaderRewriteEntries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (existing is null)
            return;

        await db.HeaderRewriteEntries.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.HeaderRuleDeleted, actor, tenantId: existing.TenantId,
            detail: $"{existing.Action} {existing.HeaderName}", ct: ct);
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
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.SenderRuleCreated, actor, tenantId: rule.TenantId,
            detail: $"{rule.FromPattern} -> {rule.ToAddress}", ct: ct);
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
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.SenderRuleUpdated, actor, tenantId: existing.TenantId,
            detail: $"{existing.FromPattern} -> {existing.ToAddress} enabled={existing.IsEnabled}", ct: ct);
    }

    public async Task DeleteSenderRuleAsync(int id, CancellationToken ct = default)
    {
        // Load-then-delete so the audit row can name the rule that was removed.
        var existing = await db.SenderRewriteEntries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (existing is null)
            return;

        await db.SenderRewriteEntries.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.SenderRuleDeleted, actor, tenantId: existing.TenantId,
            detail: $"{existing.FromPattern} -> {existing.ToAddress}", ct: ct);
    }
}

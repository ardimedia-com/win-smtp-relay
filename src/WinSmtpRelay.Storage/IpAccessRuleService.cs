using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// Mutations here change who may connect and relay — audited at the SERVICE so no caller (UI page, API
// endpoint, or background job) can change access policy without leaving a trace. The actor comes from
// the ambient ICurrentActor; a background/system scope audits with a null actor.
public class IpAccessRuleService(
    RelayDbContext db,
    IRuntimeConfigCache cache,
    ICurrentActor actor,
    IAdminAuditService audit) : IIpAccessRuleService
{
    public async Task<IReadOnlyList<IpAccessRule>> GetAllAsync(CancellationToken ct = default)
    {
        return await db.IpAccessRules.AsNoTracking().OrderBy(r => r.SortOrder).ToListAsync(ct);
    }

    public async Task<IpAccessRule> CreateAsync(IpAccessRule rule, CancellationToken ct = default)
    {
        db.IpAccessRules.Add(rule);
        await db.SaveChangesAsync(ct);
        // IP rules are read on the SMTP hot path (relay access + strict tenant binding); refresh the
        // cache here so no caller (UI, API, or background) can forget to and leave stale policy live.
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.IpRuleCreated, actor.UserId, actor.Email,
            tenantId: rule.TenantId, detail: $"{rule.Action} {rule.Network}", ct: ct);
        return rule;
    }

    public async Task UpdateAsync(IpAccessRule rule, CancellationToken ct = default)
    {
        var existing = await db.IpAccessRules.FirstOrDefaultAsync(r => r.Id == rule.Id, ct);
        if (existing is null) return;

        existing.Network = rule.Network;
        existing.Action = rule.Action;
        existing.SortOrder = rule.SortOrder;
        existing.Description = rule.Description;

        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.IpRuleUpdated, actor.UserId, actor.Email,
            tenantId: existing.TenantId, detail: $"{existing.Action} {existing.Network}", ct: ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        // Load-then-delete rather than ExecuteDelete, so the audit row can say WHICH rule was removed —
        // "iprule.deleted #17" is useless once the row is gone.
        var existing = await db.IpAccessRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (existing is null) return;

        db.IpAccessRules.Remove(existing);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        await audit.WriteAsync(AdminAuditActions.IpRuleDeleted, actor.UserId, actor.Email,
            tenantId: existing.TenantId, detail: $"{existing.Action} {existing.Network}", ct: ct);
    }
}

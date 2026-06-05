using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class IpAccessRuleService(RelayDbContext db, IRuntimeConfigCache cache) : IIpAccessRuleService
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
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await db.IpAccessRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        cache.Invalidate();
    }
}

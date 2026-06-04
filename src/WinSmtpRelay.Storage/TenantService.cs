using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class TenantService(RelayDbContext db, IRuntimeConfigCache cache) : ITenantService
{
    public async Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken cancellationToken = default)
        => await db.Tenants.AsNoTracking().OrderBy(t => t.Id).ToListAsync(cancellationToken);

    public async Task<Tenant?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        => await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<Tenant?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
        => await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);

    public async Task<Tenant> CreateAsync(string name, string slug, CancellationToken cancellationToken = default)
    {
        var normalizedSlug = NormalizeSlug(slug);
        if (await db.Tenants.AnyAsync(t => t.Slug == normalizedSlug, cancellationToken))
            throw new InvalidOperationException($"A tenant with slug '{normalizedSlug}' already exists.");

        var tenant = new Tenant
        {
            Name = string.IsNullOrWhiteSpace(name) ? normalizedSlug : name.Trim(),
            Slug = normalizedSlug,
            IsEnabled = true,
            CreatedUtc = DateTime.UtcNow
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate();
        return tenant;
    }

    public async Task UpdateAsync(int id, string name, bool isEnabled, CancellationToken cancellationToken = default)
    {
        if (id == TenantDefaults.DefaultTenantId && !isEnabled)
            throw new InvalidOperationException("The default tenant cannot be disabled.");

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tenant is null)
            return;

        var enabledChanged = tenant.IsEnabled != isEnabled;
        tenant.Name = name.Trim();
        tenant.IsEnabled = isEnabled;
        await db.SaveChangesAsync(cancellationToken);

        // The SMTP/API path caches the enabled-tenant set; refresh it when that changes.
        if (enabledChanged)
            cache.Invalidate();
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id == TenantDefaults.DefaultTenantId)
            throw new InvalidOperationException("The default tenant cannot be deleted.");

        // FK constraints are Restrict, so this throws if the tenant still owns any data.
        await db.Tenants.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken);
        cache.Invalidate();
    }

    public async Task PurgeAndDeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id == TenantDefaults.DefaultTenantId)
            throw new InvalidOperationException("The default tenant cannot be deleted.");

        if (!await db.Tenants.AnyAsync(t => t.Id == id, cancellationToken))
            return;

        // Delete every owned row before the tenant (the tenant FKs are Restrict). IgnoreQueryFilters
        // makes this independent of the ambient scope; a transaction keeps it all-or-nothing.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // DomainRoutes reference SendConnectors, so remove them first.
        await db.DomainRoutes.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.SendConnectors.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.DkimDomains.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.AcceptedDomains.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.AcceptedSenderDomains.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.IpAccessRules.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.ReceiveConnectors.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.HeaderRewriteEntries.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.SenderRewriteEntries.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.RelayUsers.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.DeliveryLogs.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.QueuedMessages.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.DailyStatistics.IgnoreQueryFilters().Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);

        // Tenant-bound but not ITenantOwned (nullable TenantId, no FK): clean up to avoid orphans.
        // Deleting admin users cascades to their Identity role/claim rows at the database level.
        await db.ApiKeys.Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);
        await db.Users.Where(x => x.TenantId == id).ExecuteDeleteAsync(cancellationToken);

        await db.Tenants.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        cache.Invalidate();
    }

    public static string NormalizeSlug(string slug)
    {
        var trimmed = (slug ?? "").Trim().ToLowerInvariant();
        var chars = trimmed.Select(c => char.IsLetterOrDigit(c) || c is '-' ? c : '-').ToArray();
        var result = new string(chars).Trim('-');
        while (result.Contains("--"))
            result = result.Replace("--", "-");
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException("Slug must contain at least one letter or digit.");
        return result;
    }
}

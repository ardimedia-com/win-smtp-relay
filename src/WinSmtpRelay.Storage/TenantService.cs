using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class TenantService(RelayDbContext db) : ITenantService
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
        return tenant;
    }

    public async Task UpdateAsync(int id, string name, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tenant is null)
            return;

        tenant.Name = name.Trim();
        tenant.IsEnabled = isEnabled;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id == TenantDefaults.DefaultTenantId)
            throw new InvalidOperationException("The default tenant cannot be deleted.");

        // FK constraints are Restrict, so this throws if the tenant still owns any data.
        await db.Tenants.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken);
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

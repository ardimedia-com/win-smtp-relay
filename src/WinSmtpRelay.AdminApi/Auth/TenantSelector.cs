using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// Marks a REST group whose writes create or change tenant-owned data, so a host-scope caller must have a
/// tenant (see <see cref="TenantSelector.RequireTenantAsync"/>). Host-level groups (sending identity, rate
/// limits, tenants, API keys) are deliberately not marked.
/// </summary>
public sealed class TenantWriteMetadata;

/// <summary>
/// Lets an API-key caller name the tenant a call acts in — the REST header <c>X-Relay-Tenant</c> and the
/// MCP <c>tenant</c> argument both go through here. A host-level key is narrowed to the named tenant (what
/// the tenant switcher does in the UI); this only ever narrows what the key already sees, it grants
/// nothing. A tenant key may name only its own tenant.
/// </summary>
public static class TenantSelector
{
    /// <summary>Returns null on success, otherwise the reason the selection was refused.</summary>
    public static async Task<string?> ApplyAsync(ICurrentTenant current, RelayDbContext db, string? requested, CancellationToken ct)
    {
        requested = requested?.Trim();
        if (string.IsNullOrEmpty(requested))
            return null;

        // Tenant is not tenant-owned, so this lookup is unfiltered in every scope.
        var slug = requested.ToLowerInvariant();
        var tenantId = int.TryParse(requested, out var id)
            ? await db.Tenants.AsNoTracking().Where(t => t.Id == id).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct)
            : await db.Tenants.AsNoTracking().Where(t => t.Slug == slug).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
        if (tenantId is null)
            return $"Tenant '{requested}' does not exist.";

        if (current.IsHostScope)
        {
            current.SetTenant(tenantId.Value);
            return null;
        }

        return current.FilterEnabled && current.FilterTenantId == tenantId.Value
            ? null
            : $"This API key is bound to tenant {current.FilterTenantId} and cannot act in tenant '{requested}'.";
    }

    /// <summary>
    /// Before a tenant WRITE: a caller still in host scope (host-level key, no tenant named) is narrowed to
    /// the only tenant when there is exactly one; with several it gets <see cref="TenantRequiredException"/>
    /// naming them. Entities default to the Default tenant, so without this a host-level write would land
    /// there silently. A caller already in a tenant scope is left alone.
    /// </summary>
    public static async Task RequireTenantAsync(ICurrentTenant current, RelayDbContext db, CancellationToken ct)
    {
        if (!current.IsHostScope || current.FilterEnabled)
            return;

        var tenants = await db.Tenants.AsNoTracking().OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.Slug }).Take(20).ToListAsync(ct);
        if (tenants.Count != 1)
            throw new TenantRequiredException(tenants.Select(t => (t.Id, t.Slug)).ToList());
        current.SetTenant(tenants[0].Id);
    }
}

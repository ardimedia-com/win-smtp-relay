using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.Storage;

public record SignupResult(bool Succeeded, string? Error = null, int UserId = 0, string? ConfirmToken = null, int TenantId = 0);

/// <summary>
/// Self-service tenant signup: creates a DISABLED tenant + a locked, unverified TenantAdmin.
/// The account activates either by confirming the emailed token or by host approval.
/// </summary>
public interface ITenantSignupService
{
    Task<SignupResult> SignUpAsync(string tenantName, string tenantSlug, string adminEmail, string password, CancellationToken ct = default);

    /// <summary>Confirms an admin via the email token; on success enables the tenant and unlocks the admin.</summary>
    Task<bool> ConfirmAsync(int userId, string token, CancellationToken ct = default);

    /// <summary>Host-approval fallback: enables the tenant and activates (confirms + unlocks) its admins.</summary>
    Task ApproveTenantAsync(int tenantId, CancellationToken ct = default);
}

public class TenantSignupService(RelayDbContext db, UserManager<AdminUser> userManager, IRuntimeConfigCache cache, IAdminMembershipService memberships) : ITenantSignupService
{
    public async Task<SignupResult> SignUpAsync(string tenantName, string tenantSlug, string adminEmail, string password, CancellationToken ct = default)
    {
        string slug;
        try { slug = TenantService.NormalizeSlug(tenantSlug); }
        catch (InvalidOperationException ex) { return new SignupResult(false, ex.Message); }

        adminEmail = adminEmail.Trim();
        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct))
            return new SignupResult(false, "That organization identifier is already taken.");
        if (await userManager.FindByEmailAsync(adminEmail) is not null)
            return new SignupResult(false, "An account with that email already exists.");

        // Tenant starts disabled (pending activation).
        var tenant = new Tenant
        {
            Name = string.IsNullOrWhiteSpace(tenantName) ? slug : tenantName.Trim(),
            Slug = slug,
            IsEnabled = false,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        var user = new AdminUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            EmailConfirmed = false,
            MustChangePassword = false,
            LockoutEnabled = true,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        var create = await userManager.CreateAsync(user, password);
        if (!create.Succeeded)
        {
            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync(ct);
            return new SignupResult(false, string.Join(" ", create.Errors.Select(e => e.Description)));
        }

        // Tenant admin of the new (disabled) tenant — access is granted via a membership (the source of truth).
        await memberships.GrantAsync(user.Id, tenant.Id, RelayRoles.TenantAdmin, grantedByUserId: null, ct: ct);
        // Locked until verified/approved — a locked account cannot sign in.
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        return new SignupResult(true, null, user.Id, token, tenant.Id);
    }

    public async Task<bool> ConfirmAsync(int userId, string token, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return false;

        // A signup admin has exactly one tenant membership — the tenant being activated.
        var tenantMembership = (await memberships.GetForUserAsync(user.Id, ct)).FirstOrDefault(m => m.TenantId is not null);
        if (tenantMembership is null)
            return false;

        var result = await userManager.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
            return false;

        await userManager.SetLockoutEndDateAsync(user, null);
        await EnableTenantAsync(tenantMembership.TenantId!.Value, ct);
        return true;
    }

    public async Task ApproveTenantAsync(int tenantId, CancellationToken ct = default)
    {
        await EnableTenantAsync(tenantId, ct);

        var memberUserIds = (await memberships.GetForTenantAsync(tenantId, ct)).Select(m => m.UserId).Distinct().ToList();
        var admins = await userManager.Users.Where(u => memberUserIds.Contains(u.Id)).ToListAsync(ct);
        foreach (var admin in admins)
        {
            if (!admin.EmailConfirmed)
            {
                admin.EmailConfirmed = true;
                await userManager.UpdateAsync(admin);
            }
            await userManager.SetLockoutEndDateAsync(admin, null);
        }
    }

    private async Task EnableTenantAsync(int tenantId, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is not null && !tenant.IsEnabled)
        {
            tenant.IsEnabled = true;
            await db.SaveChangesAsync(ct);
            // The SMTP/API path caches the enabled-tenant set; refresh it now this tenant is live.
            cache.Invalidate();
        }
    }
}

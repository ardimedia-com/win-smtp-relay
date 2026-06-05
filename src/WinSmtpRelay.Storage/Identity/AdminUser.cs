using Microsoft.AspNetCore.Identity;

namespace WinSmtpRelay.Storage.Identity;

/// <summary>
/// A web-admin login account. Separate from <see cref="Core.Models.RelayUser"/> (SMTP AUTH credentials).
/// </summary>
public class AdminUser : IdentityUser<int>
{
    /// <summary>The tenant this admin manages. Null for host-level administrators.</summary>
    public int? TenantId { get; set; }

    /// <summary>True for host-level administrators (manage tenants + infrastructure).</summary>
    public bool IsHostAdmin { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>Set when the account was seeded/reset with a temporary password and must be changed.</summary>
    public bool MustChangePassword { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

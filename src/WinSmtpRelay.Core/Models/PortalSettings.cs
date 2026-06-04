namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Host-level, runtime-editable settings for the admin/sign-in portal (single row).
/// Seeded once from appsettings, then authoritative — host admins change these in the UI
/// without restarting the service.
/// </summary>
public class PortalSettings
{
    public int Id { get; set; }

    /// <summary>Whether the anonymous self-service tenant signup page (/signup) is available.</summary>
    public bool SelfServiceSignupEnabled { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

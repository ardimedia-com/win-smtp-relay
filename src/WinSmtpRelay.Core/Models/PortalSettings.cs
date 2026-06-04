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

    /// <summary>
    /// From-address for signup verification and password-reset emails. When null/blank, the
    /// appsettings <c>AdminUi:SignupFromAddress</c> value is used as a fallback.
    /// </summary>
    public string? SignupFromAddress { get; set; }

    /// <summary>Maximum self-service signup attempts per client IP per hour (0 disables the throttle).</summary>
    public int SignupMaxAttemptsPerIpPerHour { get; set; } = 5;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

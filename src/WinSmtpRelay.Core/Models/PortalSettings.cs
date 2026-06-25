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

    /// <summary>
    /// Whether the email-based account-recovery / passwordless sign-in paths — the "Email me a sign-in
    /// link" button and the "Forgot your password?" reset flow — are offered. A high-security deployment
    /// can turn this off so control of an admin's mailbox is no longer sufficient to gain access; an
    /// admin who forgets their password is then reset by another administrator. Default on.
    /// </summary>
    public bool EmailRecoveryEnabled { get; set; } = true;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

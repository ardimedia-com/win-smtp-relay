namespace WinSmtpRelay.Core.Authorization;

/// <summary>Stable action keys for <c>AdminAuditEvent.Action</c>.</summary>
public static class AdminAuditActions
{
    public const string AdminCreated = "admin.created";
    public const string AdminDisabled = "admin.disabled";
    public const string AdminEnabled = "admin.enabled";
    public const string AdminDeleted = "admin.deleted";
    public const string AdminPasswordReset = "admin.password_reset";

    public const string MembershipGranted = "membership.granted";
    public const string MembershipRevoked = "membership.revoked";

    /// <summary>A host admin self-granted a tenant membership as an emergency override.</summary>
    public const string BreakGlassEntered = "membership.break_glass";

    public const string SignInSucceeded = "signin.succeeded";
    public const string SignInFailed = "signin.failed";
    public const string SignInLink = "signin.link_requested";

    public const string MfaEnabled = "mfa.enabled";
    public const string MfaDisabled = "mfa.disabled";
    public const string MfaRecoveryCodesRegenerated = "mfa.recovery_regenerated";
}

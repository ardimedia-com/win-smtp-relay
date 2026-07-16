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

    public const string PasskeyAdded = "passkey.added";
    public const string PasskeyRemoved = "passkey.removed";

    /// <summary>An administrator triggered an unattended software self-update (download + verified install).</summary>
    public const string ServerUpdateStarted = "server.update_started";

    // Security-relevant configuration mutations (owner decision 2026-07-16). Written by the storage
    // services themselves — the actor comes from the ambient ICurrentActor, so no caller can mutate
    // policy without leaving a trace. A null actor means the change came from a background/system
    // scope (e.g. first-run seeding), which is the honest record. Cosmetic settings are not audited.

    public const string IpRuleCreated = "iprule.created";
    public const string IpRuleUpdated = "iprule.updated";
    public const string IpRuleDeleted = "iprule.deleted";

    public const string SenderDomainCreated = "senderdomain.created";
    public const string SenderDomainVerified = "senderdomain.verified";
    public const string SenderDomainDeleted = "senderdomain.deleted";

    public const string RecipientDomainCreated = "recipientdomain.created";
    public const string RecipientDomainVerified = "recipientdomain.verified";
    public const string RecipientDomainDeleted = "recipientdomain.deleted";

    public const string SendConnectorCreated = "sendconnector.created";
    public const string SendConnectorUpdated = "sendconnector.updated";
    public const string SendConnectorDeleted = "sendconnector.deleted";

    // The one-click decisions on the Rejections page. These record the operator's DECISION with its
    // context (which client, which gate refused); the resulting config change is separately audited by
    // the service that performed it (senderdomain.created / iprule.created).
    public const string RejectionDomainAccepted = "rejection.domain_accepted";
    public const string RejectionIpAllowed = "rejection.ip_allowed";
    public const string RejectionIgnored = "rejection.ignored";
    public const string RejectionUnignored = "rejection.unignored";
}

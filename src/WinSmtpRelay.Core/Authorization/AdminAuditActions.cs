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

    // Configuration mutations. Written by the storage services themselves — the actor comes from the
    // ambient ICurrentActor, so no caller can mutate policy without leaving a trace. A null actor
    // means the change came from a background/system scope (e.g. first-run seeding), which is the
    // honest record. Coverage principle (owner decision 2026-07-19, superseding the narrower
    // "security-relevant only" decision of 2026-07-16): EVERY mutation reachable through an API key
    // is audited — with programmatic (automation/MCP) callers there is no "cosmetic" category.

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

    public const string ReceiveConnectorCreated = "receiveconnector.created";
    public const string ReceiveConnectorUpdated = "receiveconnector.updated";
    public const string ReceiveConnectorDeleted = "receiveconnector.deleted";

    public const string DkimDomainCreated = "dkimdomain.created";
    public const string DkimDomainUpdated = "dkimdomain.updated";
    public const string DkimDomainDeleted = "dkimdomain.deleted";

    public const string RouteCreated = "route.created";
    public const string RouteUpdated = "route.updated";
    public const string RouteDeleted = "route.deleted";

    public const string RateLimitsUpdated = "ratelimits.updated";

    public const string HeaderRuleCreated = "headerrule.created";
    public const string HeaderRuleUpdated = "headerrule.updated";
    public const string HeaderRuleDeleted = "headerrule.deleted";

    public const string SenderRuleCreated = "senderrule.created";
    public const string SenderRuleUpdated = "senderrule.updated";
    public const string SenderRuleDeleted = "senderrule.deleted";

    public const string TenantCreated = "tenant.created";
    public const string TenantUpdated = "tenant.updated";
    /// <summary>An empty tenant was removed (FKs are Restrict — fails if it still owns data).</summary>
    public const string TenantDeleted = "tenant.deleted";
    /// <summary>A tenant AND all its data were destructively removed (queue, logs, config, users, keys).</summary>
    public const string TenantPurged = "tenant.purged";

    public const string RelayUserCreated = "relayuser.created";
    public const string RelayUserUpdated = "relayuser.updated";
    public const string RelayUserDeleted = "relayuser.deleted";

    // API-key lifecycle: creating/deleting a credential is itself security-relevant. The key's row is
    // hard-deleted on revoke; the audit detail keeps its name + prefix so the trail survives.
    public const string ApiKeyCreated = "apikey.created";
    public const string ApiKeyUpdated = "apikey.updated";
    public const string ApiKeyDeleted = "apikey.deleted";

    // Admin queue operations on individual messages (delete is destructive; requeue re-triggers delivery).
    public const string QueueMessageDeleted = "queue.message_deleted";
    public const string QueueMessageRequeued = "queue.message_requeued";
}

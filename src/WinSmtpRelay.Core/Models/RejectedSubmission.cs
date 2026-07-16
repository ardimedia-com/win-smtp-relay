namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Why the relay refused a submission. One member per reject gate, plus the protocol-level classes
/// the SMTP library rejects before our filter runs.
/// <para>
/// This type exists because the reason cannot be recovered anywhere else: every policy gate answers
/// the client the same fixed <c>550 mailbox unavailable</c> (the library maps <c>IMailboxFilter</c>'s
/// <c>false</c> to a constant), so the wire carries no reason, and the library's
/// <c>ISessionContext.ResponseException</c> event never fires for those gates at all. The reason has
/// to be captured at the gate or it is lost. See design/observable-rejections.md.
/// </para>
/// </summary>
public enum RejectReason
{
    /// <summary>Message size exceeds the configured maximum.</summary>
    MessageTooLarge = 0,

    /// <summary>
    /// An unauthenticated session could not be attributed to a tenant. <c>Detail</c> carries the
    /// outcome (AmbiguousIp / CrossTenantDomain / Unresolved).
    /// </summary>
    TenantAttributionFailed = 1,

    /// <summary>The owning tenant is disabled.</summary>
    TenantDisabled = 2,

    /// <summary>The client IP is auto-banned after repeated failed authentication.</summary>
    IpAutoBanned = 3,

    /// <summary>Per-IP rate limit exceeded. Temporary — the sender is throttled, not refused.</summary>
    IpRateLimitExceeded = 4,

    /// <summary>Blocked by an IP access rule.</summary>
    IpBlocked = 5,

    /// <summary>Not inside any configured allowed network.</summary>
    NotInAllowedNetworks = 6,

    /// <summary>Per-sender rate limit exceeded. Temporary — the sender is throttled, not refused.</summary>
    SenderRateLimitExceeded = 7,

    /// <summary>The sender domain is not in the accepted sender domains.</summary>
    SenderDomainNotAccepted = 8,

    /// <summary>The sender domain is accepted but its ownership is not verified.</summary>
    SenderDomainNotVerified = 9,

    /// <summary>The authenticated user may not send as this address.</summary>
    SendAsDenied = 10,

    /// <summary>Per-user rate limit exceeded. Temporary — the sender is throttled, not refused.</summary>
    UserRateLimitExceeded = 11,

    /// <summary>SPF hard fail while enforcement is in Reject mode.</summary>
    SpfFail = 12,

    /// <summary>The recipient domain is hosted but its ownership is not verified.</summary>
    RecipientDomainNotVerified = 13,

    /// <summary>Open-relay protection: relaying externally needs authentication or an explicit allow rule.</summary>
    OpenRelayDenied = 14,

    /// <summary>The command line could not be parsed. The only reason that carries a raw buffer.</summary>
    CommandSyntaxError = 100,

    /// <summary>The command was valid but not allowed in the session's current state.</summary>
    CommandSequenceError = 101,

    /// <summary>A mailbox in the command could not be interpreted as an address.</summary>
    InvalidMailboxName = 102,

    /// <summary>Any other error response raised by the SMTP library (timeout, cancellation, oversize DATA).</summary>
    ProtocolOther = 103
}

public static class RejectReasonExtensions
{
    /// <summary>
    /// True for a rejection that is expected to clear on its own — the sender is being throttled, not
    /// refused. Temporary rejections are not a misconfiguration, so they are reported differently from
    /// a device that will never succeed until someone changes something.
    /// </summary>
    public static bool IsTemporary(this RejectReason reason) => reason is
        RejectReason.IpRateLimitExceeded or
        RejectReason.SenderRateLimitExceeded or
        RejectReason.UserRateLimitExceeded;

    /// <summary>A short operator-facing description of the gate that refused.</summary>
    public static string Describe(this RejectReason reason) => reason switch
    {
        RejectReason.MessageTooLarge => "message exceeds the size limit",
        RejectReason.TenantAttributionFailed => "sender could not be attributed to a tenant",
        RejectReason.TenantDisabled => "the owning tenant is disabled",
        RejectReason.IpAutoBanned => "the client IP is auto-banned after failed authentication",
        RejectReason.IpRateLimitExceeded => "per-IP rate limit exceeded",
        RejectReason.IpBlocked => "blocked by an IP access rule",
        RejectReason.NotInAllowedNetworks => "the client IP is not in an allowed network",
        RejectReason.SenderRateLimitExceeded => "per-sender rate limit exceeded",
        RejectReason.SenderDomainNotAccepted => "the sender domain is not an accepted sender domain",
        RejectReason.SenderDomainNotVerified => "the sender domain's ownership is not verified",
        RejectReason.SendAsDenied => "the user may not send as this address",
        RejectReason.UserRateLimitExceeded => "per-user rate limit exceeded",
        RejectReason.SpfFail => "SPF hard fail while enforcement is set to Reject",
        RejectReason.RecipientDomainNotVerified => "the recipient domain's ownership is not verified",
        RejectReason.OpenRelayDenied => "relaying externally requires authentication or an explicit allow rule",
        RejectReason.CommandSyntaxError => "the client sent a command the SMTP parser could not read",
        RejectReason.CommandSequenceError => "the client sent a command out of sequence",
        RejectReason.InvalidMailboxName => "the client sent an address that could not be interpreted",
        RejectReason.ProtocolOther => "the SMTP session ended with an error",
        _ => reason.ToString()
    };
}

/// <summary>
/// One aggregated row per distinct rejection, NOT one row per event: a device retrying every 30
/// minutes for six months is a single row with a large <see cref="Count"/>, not 8,000 rows.
/// <para>
/// Deliberately NOT <see cref="ITenantOwned"/> — like <see cref="AdminMembership"/>, a null
/// <see cref="TenantId"/> means host-level, which is exactly the case a tenant-attribution failure
/// produces (there is no tenant precisely because attribution is what failed). The table is therefore
/// excluded from the global tenant query filter and must be queried explicitly.
/// </para>
/// </summary>
public class RejectedSubmission
{
    public int Id { get; set; }

    /// <summary>The owning tenant, or null when no tenant could be resolved (host-level row).</summary>
    public int? TenantId { get; set; }

    /// <summary>The client IP that was refused. Part of the aggregation key.</summary>
    public required string ClientIp { get; set; }

    /// <summary>Which gate refused. Part of the aggregation key.</summary>
    public RejectReason Reason { get; set; }

    /// <summary>The SMTP reply code the client received. Part of the aggregation key.</summary>
    public int ReplyCode { get; set; }

    /// <summary>
    /// The envelope sender's domain, or <c>""</c> when none was known (protocol-level rejects, where no
    /// envelope was parsed). Part of the aggregation key: a device alternating between two sender
    /// domains is two different requests, and the one-click offer ("accept this domain") cannot exist
    /// without it.
    /// <para>
    /// Empty rather than null on purpose: ANSI/SQLite treat NULLs as DISTINCT in a unique index, so a
    /// nullable column would let every protocol-level reject insert a fresh row and silently defeat the
    /// aggregation. (A null-to-"" value converter cannot fix that — EF Core never passes null to a
    /// converter.)
    /// </para>
    /// </summary>
    public string SenderDomain { get; set; } = "";

    /// <summary>Optional context for the reason (e.g. the attribution outcome).</summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Whether the client was inside a trusted network. Evaluated and stamped at record time, never
    /// derived at query time: eviction and the health check must not re-evaluate today's rules against
    /// historical rows, and editing a rule must not silently reclassify history.
    /// </summary>
    public bool IsTrustedSource { get; set; }

    /// <summary>How many times this exact rejection has occurred (lifetime, across episodes).</summary>
    public long Count { get; set; }

    /// <summary>
    /// When the current episode started. Reset when a row reappears after a long gap, so a device that
    /// was fixed and breaks again weeks later does not inherit a stale age and trigger an instant
    /// "failing for over a day" finding.
    /// </summary>
    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>
    /// The raw command line that failed, for <see cref="RejectReason.CommandSyntaxError"/> only —
    /// the literal line the device sent, which is what turns a reject counter into a diagnosis.
    /// Always redacted and capped before it gets here (see RejectionBuffer); never a message body.
    /// </summary>
    public string? LastBuffer { get; set; }

    /// <summary>How long a row may be idle before a new occurrence counts as a fresh episode.</summary>
    public static readonly TimeSpan EpisodeResetWindow = TimeSpan.FromHours(48);
}

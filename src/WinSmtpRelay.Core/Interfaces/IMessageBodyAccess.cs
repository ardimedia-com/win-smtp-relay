namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// The single gate in front of a stored message body (customer mail content). Every read — the REST download,
/// a resend from the admin UI, anything that hands the body to a person or a key — goes through here, so the
/// consent rule is applied once and every read leaves an audit row (who, which message, which tenant).
/// <para>
/// The consent rule mirrors the admin UI's: the acting principal must hold a <b>tenant-admin membership in the
/// message's tenant</b> (granted, or self-granted as an audited break-glass). A host membership alone is never
/// enough — neither in host scope nor after narrowing a host-level API key with <c>X-Relay-Tenant</c>. An API
/// key qualifies only when it is a tenant-admin key of that very tenant.
/// </para>
/// </summary>
public interface IMessageBodyAccess
{
    /// <summary>
    /// Checks consent for the ambient actor (<see cref="ICurrentActor"/>), writes the audit row and returns the
    /// body. <paramref name="purpose"/> is a short English label stored in the audit detail (e.g. "api download",
    /// "resend"). The ambient tenant filter applies, so a message outside the caller's scope is
    /// <see cref="MessageBodyReadStatus.NotFound"/>.
    /// </summary>
    Task<MessageBodyReadResult> ReadAsync(long messageId, string purpose, CancellationToken ct = default);
}

public enum MessageBodyReadStatus
{
    Ok,
    /// <summary>No such message in the caller's scope.</summary>
    NotFound,
    /// <summary>The message exists but its body is no longer retained.</summary>
    NoBody,
    /// <summary>The caller lacks a tenant-admin membership in the message's tenant (consent model).</summary>
    Forbidden,
}

public sealed record MessageBodyReadResult(MessageBodyReadStatus Status, byte[]? Body = null, int? TenantId = null);

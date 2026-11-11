using System.Net;

namespace WinSmtpRelay.Core.Interfaces;

public interface IMxResolver
{
    /// <summary>
    /// The mail hosts for <paramref name="domain"/> in delivery order: MX records by preference (equal preferences
    /// in random order, to spread load as RFC 5321 §5.1 asks), or the domain itself when it has no MX but has an
    /// address (implicit MX). Throws <see cref="MxLookupException"/> when the domain cannot receive mail —
    /// permanently (NXDOMAIN, a null MX «.», neither MX nor address) or for now (SERVFAIL, timeout).
    /// </summary>
    Task<IReadOnlyList<string>> ResolveMxAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>The IPv4/IPv6 addresses of a mail host (an IP literal resolves to itself). Empty when it has none.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken = default);
}

/// <summary>
/// The recipient domain's mail routing could not be resolved. <see cref="IsPermanent"/> separates a domain that
/// does not accept mail (no retry, 5.1.2) from a DNS outage (retry, 4.4.3).
/// </summary>
public sealed class MxLookupException(string message, bool isPermanent, Exception? inner = null) : Exception(message, inner)
{
    public bool IsPermanent { get; } = isPermanent;
}

using System.Net.NetworkInformation;

namespace WinSmtpRelay.Core;

/// <summary>
/// Resolves the hostname the relay announces in EHLO/HELO on outbound deliveries. RFC 5321 §4.1.4
/// requires a fully qualified domain name (or an address literal); strict receivers reject an
/// unqualified name outright (observed: "550 Is neither a FQDN nor a IP literal"). The name should
/// ideally match the PTR record of the outbound IP — EHLO = PTR = SPF sender is the constellation
/// strict receivers expect, and a resolving-but-mismatched name still costs spam points.
/// </summary>
public static class EhloHostname
{
    /// <summary>
    /// Normalizes a candidate EHLO name: trims whitespace and a trailing root dot. Returns null
    /// unless the result is fully qualified (contains a dot) — a single-label name is never usable.
    /// </summary>
    public static string? Normalize(string? name)
    {
        var trimmed = name?.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(trimmed) || !trimmed.Contains('.') ? null : trimmed;
    }

    /// <summary>
    /// The machine's own FQDN (hostname + DNS suffix), or null when the machine has no DNS suffix.
    /// Never returns a bare single-label machine name.
    /// </summary>
    public static string? MachineFqdn()
    {
        var ipProperties = IPGlobalProperties.GetIPGlobalProperties();
        var host = ipProperties.HostName?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(host))
            return null;
        if (host.Contains('.'))
            return host; // already fully qualified
        var domain = ipProperties.DomainName?.Trim().Trim('.');
        return string.IsNullOrEmpty(domain) ? null : $"{host}.{domain}";
    }

    /// <summary>
    /// Picks the effective EHLO name: the first qualified candidate (per-connector override first,
    /// then the host's public hostname), falling back to the machine FQDN. Null means no usable name
    /// exists — the caller must refuse to deliver rather than announce an unqualified name.
    /// </summary>
    public static string? Resolve(string? connectorEhloDomain, string? publicHostname) =>
        Normalize(connectorEhloDomain) ?? Normalize(publicHostname) ?? MachineFqdn();
}

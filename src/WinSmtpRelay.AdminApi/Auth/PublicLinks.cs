using System.Net;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Configuration;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// Where mailed links point and which Host headers the admin plane answers. A link in a sign-in or reset mail
/// carries a live token, so it must never be built from an attacker-chosen Host header (H13): it comes from
/// <see cref="AdminUiOptions.PublicBaseUrl"/>, or — without one — from the request address, which host filtering
/// has already restricted to <see cref="DefaultAllowedHosts"/>.
/// </summary>
public static class PublicLinks
{
    /// <summary>True when <paramref name="value"/> is an absolute http(s) URL without query or fragment.</summary>
    public static bool IsValidBaseUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http"
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    /// <summary>
    /// The base (ending in <c>/</c>) to build a mailed link on, or null when no safe base exists: no public URL is
    /// configured AND host filtering accepts any host (<c>AllowedHosts = *</c>), so the request host is untrusted.
    /// </summary>
    public static string? ResolveBase(string? publicBaseUrl, IEnumerable<string>? allowedHosts, string requestBaseUri)
    {
        if (IsValidBaseUrl(publicBaseUrl))
        {
            var uri = new Uri(publicBaseUrl!.Trim());
            return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/') + "/";
        }

        var hosts = allowedHosts?.ToList() ?? [];
        if (hosts.Count == 0 || hosts.Contains("*"))
            return null;
        return requestBaseUri.EndsWith('/') ? requestBaseUri : requestBaseUri + "/";
    }

    /// <summary>Builds an absolute link for a relative path (e.g. <c>account/reset-password?…</c>), or null (see <see cref="ResolveBase"/>).</summary>
    public static string? Build(string? publicBaseUrl, IEnumerable<string>? allowedHosts, string requestBaseUri, string relativePathAndQuery)
        => ResolveBase(publicBaseUrl, allowedHosts, requestBaseUri) is { } baseUri
            ? baseUri + relativePathAndQuery.TrimStart('/')
            : null;

    /// <summary>
    /// The Host names the admin plane answers when <c>AllowedHosts</c> is not configured: loopback, this machine's
    /// name and DNS name, the address it binds to (all local addresses when it binds to any), and the host of
    /// <see cref="AdminUiOptions.PublicBaseUrl"/>. A reverse-proxy or DNS alias name must be configured
    /// (<c>AdminUi:PublicBaseUrl</c> or <c>AllowedHosts</c>).
    /// </summary>
    public static IReadOnlyList<string> DefaultAllowedHosts(AdminUiOptions options)
    {
        var hosts = new List<string> { "localhost", "127.0.0.1", "[::1]" };

        void Add(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;
            host = host.Trim().TrimEnd('.');
            if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                host = $"[{ip}]";
            if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                hosts.Add(host);
        }

        Add(Environment.MachineName);
        try
        {
            Add(Dns.GetHostEntry(string.Empty).HostName);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // No resolvable host name: the machine name above still applies.
        }

        if (IPAddress.TryParse(options.BindAddress, out var bind))
        {
            if (bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any))
            {
                foreach (var local in IpAddressScope.LocalAddresses())
                    Add(local.ToString());
            }
            else if (!IPAddress.IsLoopback(bind))
            {
                Add(bind.ToString());
            }
        }

        if (IsValidBaseUrl(options.PublicBaseUrl))
            Add(new Uri(options.PublicBaseUrl!.Trim()).Host);

        return hosts;
    }
}

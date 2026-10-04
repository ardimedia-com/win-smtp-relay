using System.Text.RegularExpressions;

namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// Rewrites the most common non-RFC envelope syntax — a bare address without angle brackets, optionally
/// padded with spaces after the colon — into the form RFC 5321 requires, so the strict SMTP parser can
/// read it:
/// <code>
/// MAIL FROM: device@example.com SIZE=123   →   MAIL FROM:&lt;device@example.com&gt; SIZE=123
/// RCPT TO: support@example.com             →   RCPT TO:&lt;support@example.com&gt;
/// </code>
/// <para>
/// Embedded firmware (storage controllers, UPSes, printers) sends this and cannot be changed. The SMTP
/// library answers it with <c>502 Unrecognized command</c> — not even a 501 — and the device retries
/// forever. This class is syntax only: it never decides whether a client may use it (that is
/// <see cref="LenientEndpointListenerFactory"/>, trusted networks only) and never decides whether the
/// mail is accepted (every policy gate in <see cref="RelayMailboxFilter"/> still runs on the result).
/// See design/lenient-address-syntax.md.
/// </para>
/// </summary>
public static partial class LenientAddressSyntax
{
    /// <summary>
    /// <c>MAIL FROM</c> / <c>RCPT TO</c>, optional blanks around the colon, then a bare token that contains
    /// an <c>@</c> and no angle bracket, then optional parameters. Already-bracketed paths (including the
    /// null reverse-path <c>&lt;&gt;</c>) deliberately do NOT match: the library parses those itself —
    /// it already skips blanks after the colon — and a line that does not match is never touched.
    /// </summary>
    [GeneratedRegex(
        @"^(?<verb>MAIL[ \t]+FROM|RCPT[ \t]+TO)[ \t]*:[ \t]*(?<address>[^ \t<>]*@[^ \t<>]*)(?<rest>[ \t].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex SloppyPath();

    /// <summary>
    /// Returns <c>true</c> and the canonical command line when <paramref name="line"/> (without its CRLF)
    /// is a MAIL FROM / RCPT TO whose address lacks angle brackets. Returns <c>false</c> for every other
    /// line — conformant ones included — which the caller must then pass through unchanged.
    /// </summary>
    public static bool TryNormalize(string line, out string normalized)
    {
        normalized = line;

        // RFC 5321 caps a command line at 512 octets; anything far beyond that is not a device quirk.
        if (line.Length is < 10 or > 1000)
            return false;

        Match match;
        try
        {
            match = SloppyPath().Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!match.Success)
            return false;

        var verb = char.ToUpperInvariant(match.Groups["verb"].Value[0]) == 'M' ? "MAIL FROM" : "RCPT TO";

        normalized = $"{verb}:<{match.Groups["address"].Value}>{match.Groups["rest"].Value}";
        return true;
    }
}

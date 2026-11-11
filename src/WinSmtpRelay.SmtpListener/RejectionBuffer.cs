using System.Text;

namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// Turns the raw bytes of a command line the SMTP parser could not read into something safe to store.
/// <para>
/// The raw line is what elevates a reject counter into a diagnosis — the record is not "a client was
/// rejected" but the literal line the device sent — so it is worth keeping. It is also the one place a
/// credential can reach the database: an <c>AUTH PLAIN &lt;base64&gt;</c> line whose payload is
/// malformed fails in the parser like any other command and carries the whole line, credential
/// included. A well-formed AUTH that merely fails authentication throws nothing and never produces a
/// buffer, and the continuation lines of an AUTH that RUNS are read by the AUTH command itself.
/// </para>
/// <para>
/// The second exposure is an AUTH that is refused before it runs (AUTH where it is not offered, e.g. on a
/// plain-text connection, or out of sequence): the client does not know that, and sends its base64 user
/// name and password as the next lines — which then reach the command parser, fail it, and arrive here
/// looking like any other garbage line. The session pipe marks the lines that follow an AUTH command line
/// (<see cref="SessionKeys.RedactRejectedLine"/>), and such a line is stored as a marker only.
/// </para>
/// <para>
/// Redaction is therefore fail-safe by prefix, not fail-open by pattern: anything that looks like an
/// AUTH command loses everything after the mechanism, without trying to locate the secret inside it.
/// A regex over the base64 payload would be the opposite bet — it fails open on every line it does not
/// recognise, which is exactly the population this buffer is made of.
/// </para>
/// </summary>
public static class RejectionBuffer
{
    /// <summary>Cap on both the raw bytes read and the stored string. The column allows 600, leaving headroom for the marker.</summary>
    public const int MaxLength = 512;

    private const string TruncationMarker = "…[truncated]";

    /// <summary>What is stored instead of a line that may be an AUTH continuation (a credential).</summary>
    public const string AuthContinuationMarker = "[redacted: line after AUTH]";

    /// <summary>
    /// Renders the failing command line for storage: AUTH lines reduced to verb + mechanism, everything
    /// else escaped to printable ASCII and capped. A line that followed an AUTH command
    /// (<paramref name="followsAuth"/>) is never stored at all. Returns null when there is nothing to store.
    /// </summary>
    public static string? Redact(byte[]? raw, bool followsAuth = false)
    {
        if (raw is null || raw.Length == 0)
            return null;

        if (followsAuth)
            return AuthContinuationMarker;

        // Truncation is tracked from the RAW length, not the rendered one: an all-printable line is
        // escaped 1:1, so a 2 KB command would render as exactly MaxLength characters and look complete.
        // A silently-cut command line is worse than no line at all — it reads as the whole thing.
        var truncated = raw.Length > MaxLength;
        var line = Escape(raw.AsSpan(0, Math.Min(raw.Length, MaxLength))).TrimStart();

        if (line.Length == 0)
            return null;

        if (line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
            return RedactAuth(line);

        // Escaping can still expand a binary line (\xNN is four characters per byte), so cap the
        // rendered string as well.
        if (line.Length > MaxLength)
        {
            line = line[..MaxLength];
            truncated = true;
        }

        return truncated ? line + TruncationMarker : line;
    }

    /// <summary>
    /// Keeps the verb and the mechanism (which is diagnostic — "the device tried AUTH PLAIN and got the
    /// syntax wrong") and discards the rest unconditionally, whether or not it parses as a credential.
    /// </summary>
    private static string RedactAuth(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Only echo a mechanism that is plainly a mechanism name; never echo an arbitrary token, which
        // could be the credential itself on a line like "AUTH <base64>".
        if (parts.Length > 1 && parts[1].Length <= 20 && parts[1].All(char.IsAsciiLetter))
            return $"AUTH {parts[1].ToUpperInvariant()} [redacted]";

        return "AUTH [redacted]";
    }

    /// <summary>
    /// Byte-wise escape to printable ASCII. Deliberately not a UTF-8 decode: this input is arbitrary
    /// bytes from an unknown device, and a decode would either throw or produce replacement characters
    /// that misrepresent what was actually on the wire. \xNN keeps the record literal and greppable.
    /// </summary>
    private static string Escape(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(raw.Length);

        foreach (var b in raw)
        {
            if (b >= 0x20 && b <= 0x7E)
                sb.Append((char)b);
            else if (b == (byte)'\r')
                sb.Append("\\r");
            else if (b == (byte)'\n')
                sb.Append("\\n");
            else if (b == (byte)'\t')
                sb.Append("\\t");
            else
                sb.Append("\\x").Append(b.ToString("X2"));
        }

        return sb.ToString();
    }
}

using System.Text;

namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// Turns the raw bytes of a command line the SMTP parser could not read into something safe to store.
/// <para>
/// The raw line is what elevates a reject counter into a diagnosis — the record is not "a client was
/// rejected" but the literal line the device sent — so it is worth keeping. It is also the one place a
/// credential can reach the database: an <c>AUTH PLAIN &lt;base64&gt;</c> line whose payload is
/// malformed fails in the parser like any other command and carries the whole line, credential
/// included. (A well-formed AUTH that merely fails authentication throws nothing and never produces a
/// buffer, and <c>AUTH LOGIN</c> sends its credentials on continuation lines that bypass the command
/// parser entirely — so this narrow case is the whole exposure.)
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

    /// <summary>
    /// Renders the failing command line for storage: AUTH lines reduced to verb + mechanism, everything
    /// else escaped to printable ASCII and capped. Returns null when there is nothing to store.
    /// </summary>
    public static string? Redact(byte[]? raw)
    {
        if (raw is null || raw.Length == 0)
            return null;

        var line = Escape(raw.AsSpan(0, Math.Min(raw.Length, MaxLength))).TrimStart();

        if (line.Length == 0)
            return null;

        if (line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
            return RedactAuth(line);

        return line.Length > MaxLength
            ? string.Concat(line.AsSpan(0, MaxLength), TruncationMarker)
            : line;
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

using System.Buffers;
using System.Text;

namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// The top-level header block of a received message, read byte-exactly: where each field starts and ends
/// in the original bytes. Used for what must be done on the raw message rather than on a parsed copy —
/// counting fields (From, Received), removing a field (a forged <c>Authentication-Results</c>), and reading
/// the Message-ID without ever looking at the body.
/// <para>
/// Grammar (RFC 5322 §2.2): a field starts on a line that does not begin with whitespace and contains a
/// colon; continuation lines begin with SP or HTAB; the block ends at the first empty line. A line that is
/// neither ends the block too (it cannot be a header), which is how MimeKit treats it as well.
/// </para>
/// </summary>
internal sealed class RawHeaderBlock
{
    public readonly record struct Field(string Name, int Start, int End, string Value);

    private RawHeaderBlock(byte[] bytes, List<Field> fields)
    {
        Bytes = bytes;
        Fields = fields;
    }

    /// <summary>The header block's bytes (everything before the empty line that separates the body).</summary>
    public byte[] Bytes { get; }

    /// <summary>The fields in their original order.</summary>
    public IReadOnlyList<Field> Fields { get; }

    /// <summary>Number of bytes of the message that belong to the header block.</summary>
    public int Length => Bytes.Length;

    public int Count(string name) => Fields.Count(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Field? First(string name)
    {
        foreach (var f in Fields)
            if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }

    public static RawHeaderBlock Read(ReadOnlySequence<byte> message)
    {
        var length = FindHeaderBlockLength(message);
        var bytes = message.Slice(0, length).ToArray();
        return new RawHeaderBlock(bytes, ParseFields(bytes));
    }

    /// <summary>Offset of the empty line that ends the header block, or the message length when there is none.</summary>
    private static int FindHeaderBlockLength(ReadOnlySequence<byte> message)
    {
        var reader = new SequenceReader<byte>(message);
        long lineStart = 0;

        while (!reader.End)
        {
            // An empty line (CRLF or a bare LF) ends the header block.
            if (reader.IsNext((byte)'\n') || reader.IsNext("\r\n"u8))
                return checked((int)lineStart);

            if (!reader.TryAdvanceTo((byte)'\n', advancePastDelimiter: true))
                break;

            lineStart = reader.Consumed;
        }

        return checked((int)message.Length);
    }

    private static List<Field> ParseFields(byte[] bytes)
    {
        var fields = new List<Field>();
        var pos = 0;
        string? name = null;
        var start = 0;

        while (pos < bytes.Length)
        {
            var lineEnd = Array.IndexOf(bytes, (byte)'\n', pos);
            var next = lineEnd < 0 ? bytes.Length : lineEnd + 1;

            if (bytes[pos] is (byte)' ' or (byte)'\t')
            {
                // Continuation of the current field (or stray whitespace before any field: ignored).
                pos = next;
                continue;
            }

            if (name is not null)
                fields.Add(MakeField(bytes, name, start, pos));

            var colon = Array.IndexOf(bytes, (byte)':', pos, next - pos);
            if (colon <= pos)
            {
                // Not a header line: the block ends here for parsing purposes.
                name = null;
                break;
            }

            name = Encoding.Latin1.GetString(bytes, pos, colon - pos).TrimEnd();
            start = pos;
            pos = next;
        }

        if (name is not null)
            fields.Add(MakeField(bytes, name, start, pos));

        return fields;
    }

    private static Field MakeField(byte[] bytes, string name, int start, int end)
    {
        var colon = Array.IndexOf(bytes, (byte)':', start, end - start);
        var raw = Encoding.Latin1.GetString(bytes, colon + 1, end - colon - 1);

        // Unfold: a field's value is one logical line (RFC 5322 §2.2.3).
        var value = raw.Replace("\r\n", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Trim();
        return new Field(name, start, end, value);
    }
}

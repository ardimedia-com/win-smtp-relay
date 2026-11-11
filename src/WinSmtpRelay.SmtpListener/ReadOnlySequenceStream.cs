using System.Buffers;

namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// A read-only, seekable <see cref="Stream"/> over a <see cref="ReadOnlySequence{T}"/>, so the received
/// message can be parsed by MimeKit straight from the session's buffer instead of from another full copy.
/// </summary>
internal sealed class ReadOnlySequenceStream(ReadOnlySequence<byte> sequence) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => sequence.Length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, sequence.Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var remaining = sequence.Length - _position;
        if (remaining <= 0 || buffer.IsEmpty)
            return 0;

        var toCopy = (int)Math.Min(buffer.Length, remaining);
        sequence.Slice(_position, toCopy).CopyTo(buffer);
        _position += toCopy;
        return toCopy;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => sequence.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

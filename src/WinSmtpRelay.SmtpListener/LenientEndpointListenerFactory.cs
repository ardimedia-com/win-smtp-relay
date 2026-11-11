using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using SmtpServer;
using SmtpServer.IO;
using SmtpServer.Net;
using SmtpServer.Protocol;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// Plugs <see cref="LenientAddressSyntax"/> into the SMTP library, for clients from a trusted network only.
/// <para>
/// Why at the pipe and not in the parser: SmtpServer v11 has no parser extension point. The session
/// constructs <c>new SmtpParser(...)</c> itself, and the replaceable <c>ISmtpCommandFactory</c> is only
/// called AFTER a successful parse — with an already-parsed mailbox — so a line the parser rejects never
/// reaches it. The one supported hook that sees the raw command line first is the
/// <see cref="IEndpointListenerFactory"/> (registered on the library's service provider): this factory
/// keeps the library's own listener, TCP and TLS handling, and only wraps the session's pipe so that a
/// sloppy MAIL FROM / RCPT TO line is presented to the unchanged parser in canonical form.
/// </para>
/// <para>
/// Everything else is untouched: conformant lines, every line from an untrusted client, and every byte
/// read while a command is executing (the DATA body, AUTH continuation lines) pass through as-is. A line
/// the normaliser cannot fix still fails in the parser and is recorded as a <c>CommandSyntaxError</c>
/// rejection exactly as before. See design/lenient-address-syntax.md.
/// </para>
/// <para>
/// Because this is the one place that sees every connection and every byte, it also carries the session
/// guards the library cannot express (<see cref="SessionGuards"/>): the concurrent-connection caps, and a
/// hard stop for a DATA body that outgrows the message size limit. The library's own strict size check
/// throws an exception that its DATA command swallows without answering, after which the rest of the body
/// would be parsed as commands.
/// </para>
/// </summary>
public sealed class LenientEndpointListenerFactory(
    Func<IPAddress, CancellationToken, ValueTask<bool>> isTrustedSource,
    ILogger logger,
    SessionGuards? guards = null) : EndpointListenerFactory
{
    private readonly SessionGuards _guards = guards ?? new SessionGuards();

    public override IEndpointListener CreateListener(IEndpointDefinition endpointDefinition) =>
        new Listener(base.CreateListener(endpointDefinition), isTrustedSource, logger, _guards);

    private sealed class Listener(
        IEndpointListener inner,
        Func<IPAddress, CancellationToken, ValueTask<bool>> isTrustedSource,
        ILogger logger,
        SessionGuards guards) : IEndpointListener
    {
        public async Task<ISecurableDuplexPipe> GetPipeAsync(ISessionContext context, CancellationToken cancellationToken)
        {
            var pipe = await inner.GetPipeAsync(context, cancellationToken).ConfigureAwait(false);
            if (pipe is null)
                return pipe!; // the library treats a null pipe as "no session"; keep that contract

            var clientIp = (context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
                ? ep as IPEndPoint
                : null)?.Address;

            Action? release = null;
            if (guards.Connections is { } limiter)
            {
                if (!limiter.TryAcquire(clientIp))
                {
                    // Refused off the accept loop: a client that does not read its reply must not stall the
                    // listener for everyone else.
                    _ = RefuseAsync(pipe, context, clientIp);
                    return null!; // no session for this connection; the library goes on accepting
                }

                release = () => limiter.Release(clientIp);
            }

            return new LenientDuplexPipe(pipe, context, isTrustedSource, logger, guards, release);
        }

        private async Task RefuseAsync(ISecurableDuplexPipe pipe, ISessionContext context, IPAddress? clientIp)
        {
            try
            {
                logger.LogWarning("Connection from {ClientIp} refused: concurrent-connection limit reached", clientIp);
                guards.OnRejected?.Invoke(context, RejectReason.TooManyConnections, (int)SmtpReplyCode.ServiceUnavailable);

                // On an implicit-TLS endpoint the handshake has not happened yet, so a plain-text reply would
                // be garbage to the client: just close.
                if (!context.EndpointDefinition.IsSecure)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await pipe.Output.WriteReplyAsync(SessionGuards.TooManyConnectionsResponse, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Refusing a connection over the limit failed");
            }
            finally
            {
                pipe.Dispose();
            }
        }

        public void Dispose() => inner.Dispose();
    }
}

/// <summary>Session limits enforced at the pipe (see <see cref="LenientEndpointListenerFactory"/>).</summary>
public sealed class SessionGuards
{
    /// <summary>421 for a connection beyond the concurrent-session caps (RFC 5321 section 4.2.3: service not available).</summary>
    public static readonly SmtpResponse TooManyConnectionsResponse =
        new(SmtpReplyCode.ServiceUnavailable, "4.7.0 Too many connections, try again later");

    /// <summary>552 for a DATA body over the size limit; the session is closed after it.</summary>
    public static readonly SmtpResponse MessageTooLargeResponse =
        new(SmtpReplyCode.SizeLimitExceeded, "5.3.4 Message size exceeds fixed maximum message size");

    /// <summary>Concurrent-session caps; null = unlimited.</summary>
    public ConnectionLimiter? Connections { get; init; }

    /// <summary>
    /// The most bytes a DATA body may occupy in the receive buffer before the session is stopped; 0 = no
    /// guard. Set slightly above the message size limit (the buffer also holds the end-of-data marker and
    /// pipelined commands); the exact limit is checked again when the message is stored.
    /// </summary>
    public long MaxDataBytes { get; init; }

    /// <summary>Called when a guard refuses: the session, the reason, and the reply code sent.</summary>
    public Action<ISessionContext, RejectReason, int>? OnRejected { get; init; }
}

/// <summary>
/// The session's pipe with a normalising <see cref="Input"/>. Output, TLS state and the STARTTLS / implicit
/// TLS upgrade are forwarded to the library's own pipe; after an upgrade the library swaps its reader, and
/// <see cref="Input"/> re-wraps the new one, so the TLS session gets the same treatment as plain text.
/// </summary>
internal sealed class LenientDuplexPipe : ISecurableDuplexPipe
{
    private readonly ISecurableDuplexPipe _inner;
    private readonly ISessionContext _context;
    private readonly Func<IPAddress, CancellationToken, ValueTask<bool>> _isTrustedSource;
    private readonly ILogger _logger;
    private readonly SessionGuards _guards;
    private Action? _release;
    private PipeReader? _wrappedReader;
    private LenientCommandReader? _input;
    private bool? _isTrusted;

    /// <summary>Command lines still to be treated as possible AUTH continuations (see <see cref="ObserveCommandLine"/>).</summary>
    private int _authContinuationLines;

    public LenientDuplexPipe(
        ISecurableDuplexPipe inner,
        ISessionContext context,
        Func<IPAddress, CancellationToken, ValueTask<bool>> isTrustedSource,
        ILogger logger,
        SessionGuards? guards = null,
        Action? release = null)
    {
        _inner = inner;
        _context = context;
        _isTrustedSource = isTrustedSource;
        _logger = logger;
        _guards = guards ?? new SessionGuards();
        _release = release;

        // "Is a command executing?" is the line between command lines (normalise) and everything a command
        // reads itself — the DATA body, AUTH continuations — which must reach the library byte for byte.
        // A command that throws never raises CommandExecuted, but the session loop raises ResponseException
        // for it before reading the next command, so that event closes the window too.
        context.CommandExecuting += (_, e) =>
        {
            CommandExecuting = true;
            DataExecuting = e.Command is DataCommand;
        };
        context.CommandExecuted += (_, e) =>
        {
            CommandExecuting = false;
            DataExecuting = false;

            // An AUTH that RAN read its own continuation lines; nothing after it is a credential.
            if (e.Command is AuthCommand)
                _authContinuationLines = 0;
        };
        context.ResponseException += (_, _) =>
        {
            CommandExecuting = false;
            DataExecuting = false;
        };
    }

    internal bool CommandExecuting { get; private set; }

    /// <summary>True while the DATA command reads the message body.</summary>
    internal bool DataExecuting { get; private set; }

    /// <summary>Set once the session was stopped by a guard; every further read reports end of stream.</summary>
    internal bool Aborted { get; private set; }

    internal long MaxDataBytes => _guards.MaxDataBytes;

    /// <summary>The cached trust decision; <c>null</c> until the first candidate line forced it.</summary>
    internal bool? IsTrusted => _isTrusted;

    public PipeReader Input
    {
        get
        {
            var current = _inner.Input;
            if (!ReferenceEquals(current, _wrappedReader))
            {
                _wrappedReader = current;
                _input = new LenientCommandReader(current, this);
            }

            return _input!;
        }
    }

    public PipeWriter Output => _inner.Output;

    public bool IsSecure => _inner.IsSecure;

    public SslProtocols SslProtocol => _inner.SslProtocol;

    public Task UpgradeAsync(X509Certificate certificate, SslProtocols protocols, CancellationToken cancellationToken = default) =>
        _inner.UpgradeAsync(certificate, protocols, cancellationToken);

    public void Dispose()
    {
        // The library disposes the pipe once per session, but a guard must never release a slot twice.
        Interlocked.Exchange(ref _release, null)?.Invoke();
        _inner.Dispose();
    }

    /// <summary>
    /// Stops the session because the DATA body outgrew the size limit: answers 552 directly (the library's
    /// DATA command would swallow the exception without a reply) and makes every further read report end of
    /// stream, so the session ends instead of parsing the rest of the body as commands.
    /// </summary>
    internal async ValueTask AbortOversizeAsync(CancellationToken cancellationToken)
    {
        Aborted = true;
        var ep = _context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var value) ? value : "unknown";
        _logger.LogWarning("Message from {RemoteEndPoint} rejected: DATA exceeded the {Limit}-byte buffer limit; closing the session",
            ep, _guards.MaxDataBytes);

        try
        {
            _guards.OnRejected?.Invoke(_context, RejectReason.MessageTooLarge, (int)SmtpReplyCode.SizeLimitExceeded);
            await _inner.Output.WriteReplyAsync(SessionGuards.MessageTooLargeResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Answering an oversize DATA failed");
        }
    }

    /// <summary>
    /// Sees each command line as it reaches the parser. An AUTH line opens a window of two lines (AUTH LOGIN
    /// sends user and password, AUTH PLAIN one combined line) that may be credentials if the AUTH is refused
    /// before it runs; the window closes when an AUTH actually executes. The verdict for the current line is
    /// published in <see cref="SessionKeys.RedactRejectedLine"/> for the rejection recorder.
    /// </summary>
    internal void ObserveCommandLine(ReadOnlySequence<byte> buffer)
    {
        var window = buffer.Length > LenientCommandReader.MaxCommandLineBytes
            ? buffer.Slice(0, LenientCommandReader.MaxCommandLineBytes)
            : buffer;
        var reader = new SequenceReader<byte>(window);
        var complete = reader.TryReadTo(out ReadOnlySequence<byte> line, "\r\n"u8, advancePastDelimiter: false);
        if (!complete)
        {
            // Not the whole line yet (or a line longer than the window): decide conservatively, consume nothing.
            if (window.Length < LenientCommandReader.MaxCommandLineBytes)
                return;
            line = window;
        }

        if (IsAuthCommand(line))
        {
            _authContinuationLines = 2;
            _context.Properties[SessionKeys.RedactRejectedLine] = false; // the AUTH line itself is prefix-redacted
            return;
        }

        var redact = _authContinuationLines > 0;
        if (redact && complete)
            _authContinuationLines--;
        _context.Properties[SessionKeys.RedactRejectedLine] = redact;
    }

    private static bool IsAuthCommand(ReadOnlySequence<byte> line)
    {
        if (line.Length < 4)
            return false;

        Span<byte> head = stackalloc byte[5];
        var n = (int)Math.Min(5, line.Length);
        line.Slice(0, n).CopyTo(head);

        return (head[0] | 0x20) == 'a' && (head[1] | 0x20) == 'u' && (head[2] | 0x20) == 't' && (head[3] | 0x20) == 'h'
            && (n == 4 || head[4] is (byte)' ' or (byte)'\t');
    }

    /// <summary>
    /// Decided lazily — only when a sloppy line actually arrives — so ordinary sessions, and the accept
    /// loop, never pay for it; then cached for the session. Fails closed: no client IP, or a failing
    /// lookup, means strict parsing (the pre-existing behaviour), never leniency.
    /// </summary>
    internal async ValueTask<bool> IsTrustedAsync()
    {
        if (_isTrusted is { } known)
            return known;

        var clientIp = (_context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
            ? ep as IPEndPoint
            : null)?.Address;

        var trusted = false;
        if (clientIp is not null)
        {
            try
            {
                // No cancellation token on purpose: the reader already holds an un-advanced buffer from the
                // library's pipe, and a throw here would leave that pipe mid-read. The lookup is a cache read.
                trusted = await _isTrustedSource(clientIp, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Trust lookup for lenient address syntax failed; parsing strictly");
            }
        }

        _isTrusted = trusted;
        return trusted;
    }

    internal void OnNormalized(string original, string normalized)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var ep = _context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var value) ? value : "unknown";
            _logger.LogDebug("Normalised non-RFC 5321 command from {RemoteEndPoint}: '{Original}' → '{Normalized}'",
                ep, original, normalized);
        }
    }
}

/// <summary>
/// A <see cref="PipeReader"/> over the library's reader that substitutes ONE line — the command line at
/// the head of the buffer — when it is a sloppy MAIL FROM / RCPT TO from a trusted client.
/// <para>
/// It never reads ahead: every read is the library's own read, so nothing is buffered beyond what the
/// library would have buffered anyway (STARTTLS depends on that). When it substitutes, the consumer gets
/// a buffer holding exactly the rewritten line plus CRLF; consuming that buffer completely advances the
/// inner reader past the ORIGINAL line, so pipelined commands behind it are untouched and are each
/// inspected in turn when they reach the head of the buffer. The library reads one line per call and
/// always consumes it whole once it has seen the CRLF; any other consumption re-presents the line.
/// </para>
/// </summary>
internal sealed class LenientCommandReader(PipeReader inner, LenientDuplexPipe owner) : PipeReader
{
    /// <summary>Only this much of the buffer is searched for the end of the line (RFC 5321: 512 octets).</summary>
    internal const int MaxCommandLineBytes = 1024;

    private static readonly ReadResult EndOfStream = new(ReadOnlySequence<byte>.Empty, isCanceled: false, isCompleted: true);

    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;

    private bool _substituting;
    private ReadOnlySequence<byte> _substitute;
    private SequencePosition _originalStart;
    private SequencePosition _originalLineEnd;

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (owner.Aborted)
            return EndOfStream;

        var result = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (owner.DataExecuting && owner.MaxDataBytes > 0 && result.Buffer.Length > owner.MaxDataBytes)
        {
            // Discard what was received and stop the session. The exception unwinds the library's DATA command
            // (which swallows it); the next command read then sees end of stream and the session ends.
            inner.AdvanceTo(result.Buffer.End);
            await owner.AbortOversizeAsync(cancellationToken).ConfigureAwait(false);
            throw new SmtpResponseException(SessionGuards.MessageTooLargeResponse, quit: true);
        }

        if (!owner.CommandExecuting && !result.IsCanceled && !result.Buffer.IsEmpty)
            owner.ObserveCommandLine(result.Buffer);

        if (!TryFindCandidate(result, out var original, out var normalized, out var lineEnd))
            return result;

        if (!await owner.IsTrustedAsync().ConfigureAwait(false))
            return result;

        return Substitute(result, original, normalized, lineEnd);
    }

    public override bool TryRead(out ReadResult result)
    {
        if (owner.Aborted)
        {
            result = EndOfStream;
            return true;
        }

        if (!inner.TryRead(out result))
            return false;

        // Synchronous path: only a trust decision already made can be used; otherwise stay strict.
        if (owner.IsTrusted == true && TryFindCandidate(result, out var original, out var normalized, out var lineEnd))
            result = Substitute(result, original, normalized, lineEnd);

        return true;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        if (!_substituting)
        {
            inner.AdvanceTo(consumed, examined);
            return;
        }

        var consumedWholeLine = _substitute.GetOffset(consumed) >= _substitute.Length;
        _substituting = false;
        _substitute = default;

        inner.AdvanceTo(consumedWholeLine ? _originalLineEnd : _originalStart);
    }

    public override void CancelPendingRead() => inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    private bool TryFindCandidate(ReadResult result, out string original, out string normalized, out SequencePosition lineEnd)
    {
        original = normalized = "";
        lineEnd = default;

        if (owner.CommandExecuting || result.IsCanceled || result.Buffer.IsEmpty)
            return false;

        var buffer = result.Buffer;

        // Cheap pre-filter before any search or allocation: only MAIL / RCPT can be rewritten.
        Span<byte> first = stackalloc byte[1];
        buffer.Slice(0, 1).CopyTo(first);
        if ((first[0] | 0x20) is not ((byte)'m' or (byte)'r'))
            return false;

        var window = buffer.Length > MaxCommandLineBytes ? buffer.Slice(0, MaxCommandLineBytes) : buffer;
        var reader = new SequenceReader<byte>(window);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> line, Crlf, advancePastDelimiter: true))
            return false;

        // Latin-1 maps every byte to one char and back, so non-ASCII bytes survive the round trip intact.
        original = Encoding.Latin1.GetString(line);
        if (!LenientAddressSyntax.TryNormalize(original, out normalized))
            return false;

        // A position inside the window is a position inside the buffer: a slice shares its segments.
        lineEnd = reader.Position;
        return true;
    }

    private ReadResult Substitute(ReadResult result, string original, string normalized, SequencePosition lineEnd)
    {
        _substitute = new ReadOnlySequence<byte>(Encoding.Latin1.GetBytes(normalized + "\r\n"));
        _originalStart = result.Buffer.Start;
        _originalLineEnd = lineEnd;
        _substituting = true;

        owner.OnNormalized(original, normalized);

        // IsCompleted is passed through: the library refuses to process a completed read, and that must
        // stay identical for a rewritten line.
        return new ReadResult(_substitute, isCanceled: false, isCompleted: result.IsCompleted);
    }
}

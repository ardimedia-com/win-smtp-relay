using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace WinSmtpRelay.Delivery;

/// <summary>Outcome of a smart-host connectivity probe.</summary>
public enum SmartHostProbeOutcome
{
    /// <summary>The first attempt connected (and authenticated, when credentials are set).</summary>
    Reachable,

    /// <summary>A later attempt succeeded after at least one failed one — a transient problem, not an outage.</summary>
    ReachableAfterRetry,

    /// <summary>Every attempt failed.</summary>
    Unreachable,

    /// <summary>Connected, but the smart host rejected the credentials (not retried — retrying cannot fix it).</summary>
    AuthenticationRejected,
}

/// <param name="Number">1-based attempt number.</param>
/// <param name="Cause">Why the attempt failed (DNS, timeout, TLS with the certificate chain status, ...); null on success.</param>
public sealed record SmartHostProbeAttempt(int Number, bool Success, string? Cause);

public sealed record SmartHostProbeResult(SmartHostProbeOutcome Outcome, IReadOnlyList<SmartHostProbeAttempt> Attempts)
{
    /// <summary>The cause of the first failed attempt (null when none failed).</summary>
    public string? FirstFailure => Attempts.FirstOrDefault(a => !a.Success)?.Cause;

    /// <summary>"attempt 1: …; attempt 2: …" — the cause of every failed attempt.</summary>
    public string DescribeFailures() =>
        string.Join("; ", Attempts.Where(a => !a.Success).Select(a => $"attempt {a.Number}: {a.Cause}"));
}

/// <summary>Thrown by a probe attempt when the smart host rejected the credentials.</summary>
public sealed class SmartHostAuthenticationRejectedException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Smart-host connectivity probe for the self-check: connects (and authenticates, when credentials are set)
/// without sending anything, up to <c>maxAttempts</c> times with a pause between attempts, and reports the real
/// cause of each failure. A single failed attempt — a DNS hiccup, a timeout, a dropped TLS handshake while the
/// machine wakes from standby — is a transient problem; only when every attempt fails is the host unreachable.
/// </summary>
public static class SmartHostProbe
{
    /// <summary>
    /// Runs <paramref name="attempt"/> up to <paramref name="maxAttempts"/> times, pausing <paramref name="retryDelay"/>
    /// between attempts, until one succeeds. An authentication rejection ends the probe at once.
    /// </summary>
    /// <param name="attempt">One connection attempt; throws on failure.</param>
    /// <param name="describe">Turns an attempt's exception into its cause text.</param>
    /// <param name="delay">The pause primitive (injectable for tests).</param>
    public static async Task<SmartHostProbeResult> RunAsync(Func<CancellationToken, Task> attempt, int maxAttempts,
        TimeSpan retryDelay, Func<Exception, string> describe, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        maxAttempts = Math.Max(1, maxAttempts);
        var attempts = new List<SmartHostProbeAttempt>();

        for (var n = 1; n <= maxAttempts; n++)
        {
            if (n > 1)
                await delay(retryDelay, ct);
            try
            {
                await attempt(ct);
                attempts.Add(new SmartHostProbeAttempt(n, true, null));
                return new SmartHostProbeResult(n == 1 ? SmartHostProbeOutcome.Reachable : SmartHostProbeOutcome.ReachableAfterRetry, attempts);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the host is shutting down / the run was abandoned
            }
            catch (SmartHostAuthenticationRejectedException ex)
            {
                attempts.Add(new SmartHostProbeAttempt(n, false, describe(ex)));
                return new SmartHostProbeResult(SmartHostProbeOutcome.AuthenticationRejected, attempts);
            }
            catch (Exception ex)
            {
                attempts.Add(new SmartHostProbeAttempt(n, false, describe(ex)));
            }
        }
        return new SmartHostProbeResult(SmartHostProbeOutcome.Unreachable, attempts);
    }

    /// <summary>
    /// One real connection attempt with MailKit: connect with the connector's TLS mode, authenticate when
    /// <paramref name="username"/> is set, disconnect. Throws on failure; a rejected login throws
    /// <see cref="SmartHostAuthenticationRejectedException"/>. A TLS failure carries the certificate chain status
    /// seen during the handshake (see <see cref="Describe"/>).
    /// </summary>
    public static async Task ConnectOnceAsync(string host, int port, bool opportunisticTls, bool requireTls,
        string? username, string? password, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var client = new ProbeSmtpClient { Timeout = (int)timeout.TotalMilliseconds };
        var secure = requireTls ? SecureSocketOptions.StartTls
            : opportunisticTls ? SecureSocketOptions.StartTlsWhenAvailable
            : SecureSocketOptions.Auto;
        try
        {
            await client.ConnectAsync(host, port, secure, cts.Token);
            if (!string.IsNullOrWhiteSpace(username))
            {
                try
                {
                    await client.AuthenticateAsync(username, password ?? "", cts.Token);
                }
                catch (AuthenticationException ex)
                {
                    throw new SmartHostAuthenticationRejectedException($"authentication rejected: {ex.Message}", ex);
                }
            }
            try { await client.DisconnectAsync(true, cts.Token); } catch { /* best-effort */ }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer within {timeout.TotalSeconds:F0}s", ex);
        }
        catch (SslHandshakeException ex) when (client.ChainStatus is { Length: > 0 } status)
        {
            throw new SslHandshakeException($"{ex.Message} [chain status: {status}]", ex);
        }
    }

    /// <summary>
    /// The real cause of a failed attempt, in operator terms: DNS (host unknown), timeout, connection refused/
    /// reset/closed, TLS (with the certificate chain status), authentication, or the SMTP reply.
    /// </summary>
    public static string Describe(Exception ex)
    {
        if (ex is SmartHostAuthenticationRejectedException)
            return ex.Message;
        if (FindInChain<SocketException>(ex) is { } se)
            return se.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData or SocketError.NoRecovery =>
                    $"DNS: host unknown ({se.Message})",
                SocketError.TimedOut => $"timeout: TCP connect timed out ({se.Message})",
                SocketError.ConnectionRefused => $"connection refused ({se.Message})",
                SocketError.ConnectionReset or SocketError.ConnectionAborted => $"connection reset by the server ({se.Message})",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
                    $"network unreachable ({se.Message})",
                _ => $"network error {se.SocketErrorCode} ({se.Message})",
            };
        if (FindInChain<SslHandshakeException>(ex) is { } tls)
            return $"TLS handshake failed: {Flatten(tls.Message)}";
        if (FindInChain<TimeoutException>(ex) is { } te)
            return $"timeout: {te.Message}";
        if (ex is OperationCanceledException)
            return "timeout: the attempt was cancelled before the server answered";
        if (FindInChain<SmtpCommandException>(ex) is { } cmd)
            return $"SMTP {(int)cmd.StatusCode}: {Flatten(cmd.Message)}";
        if (FindInChain<SmtpProtocolException>(ex) is { } proto)
            return $"connection closed: {Flatten(proto.Message)}";
        if (FindInChain<ServiceNotConnectedException>(ex) is { } nc)
            return $"connection closed: {Flatten(nc.Message)}";
        if (FindInChain<IOException>(ex) is { } io)
            return $"connection closed: {Flatten(io.Message)}";
        return $"{ex.GetType().Name}: {Flatten(ex.Message)}";
    }

    private static T? FindInChain<T>(Exception ex) where T : Exception
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is T match)
                return match;
        if (ex is AggregateException agg)
            foreach (var inner in agg.Flatten().InnerExceptions)
                if (FindInChain<T>(inner) is { } m)
                    return m;
        return null;
    }

    // MailKit's TLS messages are multi-line bullet lists; one line reads better in a finding.
    private static string Flatten(string message) =>
        string.Join(" ", message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// SmtpClient that keeps MailKit's default certificate validation but records the chain status it saw, so a
    /// TLS failure can name the reason (e.g. RevocationStatusUnknown, UntrustedRoot, NotTimeValid).
    /// </summary>
    private sealed class ProbeSmtpClient : SmtpClient
    {
        public string? ChainStatus { get; private set; }

        public ProbeSmtpClient()
        {
            ServerCertificateValidationCallback = Validate;
        }

        private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (errors != SslPolicyErrors.None)
            {
                var statuses = chain?.ChainStatus
                    .Where(s => s.Status != X509ChainStatusFlags.NoError)
                    .Select(s => s.Status.ToString())
                    .Distinct()
                    .ToList() ?? [];
                ChainStatus = statuses.Count > 0 ? $"{errors}: {string.Join(", ", statuses)}" : errors.ToString();
            }
            return DefaultServerCertificateValidationCallback(sender, certificate!, chain!, errors);
        }
    }
}

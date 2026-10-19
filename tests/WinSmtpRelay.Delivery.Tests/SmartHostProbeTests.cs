using System.Net.Sockets;
using MailKit.Security;

namespace WinSmtpRelay.Delivery.Tests;

/// <summary>
/// The self-check's smart-host probe (T245): up to 3 attempts with a pause, the real cause per attempt, and a
/// WARNING instead of an ERROR when a retry succeeds (WUHARRYHOME 17.–19.10.2026: «Der angegebene Host ist
/// unbekannt» / SslHandshakeException / timeout on the first attempt, fine a few minutes later).
/// </summary>
[TestClass]
public class SmartHostProbeTests
{
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(45);

    private static Func<CancellationToken, Task> Sequence(params Exception?[] outcomes)
    {
        var i = 0;
        return _ =>
        {
            var e = outcomes[Math.Min(i++, outcomes.Length - 1)];
            return e is null ? Task.CompletedTask : Task.FromException(e);
        };
    }

    private static async Task<(SmartHostProbeResult Result, List<TimeSpan> Pauses)> RunAsync(Func<CancellationToken, Task> attempt)
    {
        var pauses = new List<TimeSpan>();
        var result = await SmartHostProbe.RunAsync(attempt, 3, Pause, SmartHostProbe.Describe, CancellationToken.None,
            (d, _) => { pauses.Add(d); return Task.CompletedTask; });
        return (result, pauses);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FirstAttemptSucceeds_Reachable_NoPause()
    {
        var (result, pauses) = await RunAsync(Sequence((Exception?)null));
        Assert.AreEqual(SmartHostProbeOutcome.Reachable, result.Outcome);
        Assert.AreEqual(0, pauses.Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RetrySucceeds_ReachableAfterRetry_WithFirstCause()
    {
        var (result, pauses) = await RunAsync(Sequence(new SocketException((int)SocketError.HostNotFound), null));

        Assert.AreEqual(SmartHostProbeOutcome.ReachableAfterRetry, result.Outcome);
        Assert.AreEqual(2, result.Attempts.Count);
        StringAssert.StartsWith(result.FirstFailure, "DNS: host unknown");
        CollectionAssert.AreEqual(new[] { Pause }, pauses);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task AllAttemptsFail_Unreachable_WithEveryCause()
    {
        var (result, pauses) = await RunAsync(Sequence(
            new TimeoutException("no answer within 12s"),
            new SslHandshakeException("An error occurred while attempting to establish an SSL or TLS connection.\n[chain status: RemoteCertificateChainErrors: RevocationStatusUnknown]"),
            new SocketException((int)SocketError.HostNotFound)));

        Assert.AreEqual(SmartHostProbeOutcome.Unreachable, result.Outcome);
        Assert.AreEqual(3, result.Attempts.Count);
        Assert.AreEqual(2, pauses.Count, "30–60 s between attempts, none after the last");
        var text = result.DescribeFailures();
        StringAssert.Contains(text, "attempt 1: timeout: no answer within 12s");
        StringAssert.Contains(text, "attempt 2: TLS handshake failed:");
        StringAssert.Contains(text, "RevocationStatusUnknown");
        StringAssert.Contains(text, "attempt 3: DNS: host unknown");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task AuthenticationRejected_IsNotRetried()
    {
        var (result, pauses) = await RunAsync(Sequence(
            new SmartHostAuthenticationRejectedException("authentication rejected: 535 5.7.8 bad credentials", new AuthenticationException("535"))));

        Assert.AreEqual(SmartHostProbeOutcome.AuthenticationRejected, result.Outcome);
        Assert.AreEqual(1, result.Attempts.Count);
        Assert.AreEqual(0, pauses.Count);
        StringAssert.Contains(result.FirstFailure, "535");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Describe_NamesTheRealCause()
    {
        StringAssert.StartsWith(SmartHostProbe.Describe(new SocketException((int)SocketError.ConnectionRefused)), "connection refused");
        StringAssert.StartsWith(SmartHostProbe.Describe(new IOException("outer", new SocketException((int)SocketError.ConnectionReset))), "connection reset");
        StringAssert.StartsWith(SmartHostProbe.Describe(new TaskCanceledException("A task was canceled.")), "timeout");
        StringAssert.StartsWith(SmartHostProbe.Describe(new MailKit.Net.Smtp.SmtpProtocolException("The SMTP server has unexpectedly disconnected.")), "connection closed");
        StringAssert.StartsWith(SmartHostProbe.Describe(new SslHandshakeException("line one\nline two")), "TLS handshake failed: line one line two");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Cancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SmartHostProbe.RunAsync(
            ct => Task.FromCanceled(ct), 3, Pause, SmartHostProbe.Describe, cts.Token, (_, _) => Task.CompletedTask));
    }
}

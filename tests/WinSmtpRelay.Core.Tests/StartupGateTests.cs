namespace WinSmtpRelay.Core.Tests;

/// <summary>The first self-check after start waits for network/DNS, bounded (T245).</summary>
[TestClass]
public class StartupGateTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task WaitsUntilTheProbeSucceeds()
    {
        var calls = 0;
        var waited = TimeSpan.Zero;
        var ready = await StartupGate.WaitUntilReadyAsync(
            _ => Task.FromResult(++calls >= 3), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), CancellationToken.None,
            (d, _) => { waited += d; return Task.CompletedTask; });

        Assert.IsTrue(ready);
        Assert.AreEqual(3, calls);
        Assert.AreEqual(TimeSpan.FromSeconds(30), waited);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GivesUpAfterTheMaximumWait_AndTreatsExceptionsAsNotReady()
    {
        var waited = TimeSpan.Zero;
        var ready = await StartupGate.WaitUntilReadyAsync(
            _ => throw new System.Net.Sockets.SocketException(11001), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(25), CancellationToken.None,
            (d, _) => { waited += d; return Task.CompletedTask; });

        Assert.IsFalse(ready);
        Assert.AreEqual(TimeSpan.FromMinutes(1), waited, "never waits longer than the maximum");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ReadyAtOnce_DoesNotWait()
    {
        var waited = false;
        Assert.IsTrue(await StartupGate.WaitUntilReadyAsync(_ => Task.FromResult(true), TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(15), CancellationToken.None, (_, _) => { waited = true; return Task.CompletedTask; }));
        Assert.IsFalse(waited);
    }
}

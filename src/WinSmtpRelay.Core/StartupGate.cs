namespace WinSmtpRelay.Core;

/// <summary>
/// Waits until a readiness probe succeeds (e.g. "the network is up and DNS answers"), bounded by a maximum wait.
/// Used before the first self-check after service start: right after boot or wake-up from standby the network
/// is often not ready yet, and a run at that moment reports every smart host as unreachable.
/// </summary>
public static class StartupGate
{
    /// <summary>
    /// Calls <paramref name="probe"/> until it returns true, pausing <paramref name="interval"/> between calls, for at
    /// most <paramref name="maxWait"/>. Returns true when the probe succeeded, false when the time ran out (the
    /// caller proceeds anyway). A probe that throws counts as "not ready".
    /// </summary>
    /// <param name="delay">The pause primitive (injectable for tests); defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public static async Task<bool> WaitUntilReadyAsync(Func<CancellationToken, Task<bool>> probe, TimeSpan maxWait,
        TimeSpan interval, CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var waited = TimeSpan.Zero;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool ready;
            try { ready = await probe(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { ready = false; }
            if (ready)
                return true;
            if (waited >= maxWait)
                return false;

            var pause = maxWait - waited < interval ? maxWait - waited : interval;
            await delay(pause, ct);
            waited += pause;
        }
    }
}

using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core;

/// <summary>
/// Simple in-process <see cref="IActivityFeed"/>. Handlers run on the publisher's thread, so each
/// subscriber (a Blazor circuit) marshals to its dispatcher and keeps the handler fast; a faulty
/// subscriber is isolated so it cannot stall publishing for the others.
/// </summary>
public sealed class InProcessActivityFeed : IActivityFeed
{
    public event Action<ActivityEvent>? Received;

    public void Publish(ActivityEvent activityEvent)
    {
        // Snapshot the multicast delegate so a concurrent subscribe/unsubscribe can't race the invocation,
        // and isolate each subscriber so one throwing handler doesn't break delivery to the rest.
        var handlers = Received;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<ActivityEvent>>())
        {
            try { handler(activityEvent); }
            catch { /* a faulty subscriber must not break publishing for others */ }
        }
    }
}

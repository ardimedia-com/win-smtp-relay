using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// In-process publish/subscribe for <see cref="ActivityEvent"/>s. The relay backend publishes events;
/// the server-rendered Blazor admin pages subscribe directly, so the Live Activity feed and the
/// dashboard/queue live updates work without a server-to-self SignalR connection (which can't carry the
/// signed-in user's authentication/tenant). Registered as a singleton.
/// </summary>
public interface IActivityFeed
{
    /// <summary>Raised for every published event, on the publisher's thread. Subscribers must marshal to
    /// their own context (e.g. Blazor's <c>InvokeAsync</c>) and keep the handler fast and exception-safe.</summary>
    event Action<ActivityEvent>? Received;

    void Publish(ActivityEvent activityEvent);
}

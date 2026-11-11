using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public static class CurrentTenantExtensions
{
    /// <summary>
    /// Whether an activity event belongs to this scope: host scope sees everything, a tenant scope sees its own
    /// tenant's events, and an untenanted event is visible to everyone — except an SMTP connection event, which only
    /// the host scope sees (a connection is not attributed to a tenant yet, and its client IP may belong to any
    /// tenant's devices; the SignalR hub sends it to host administrators only for the same reason). The single
    /// definition for every page that subscribes to <see cref="IActivityFeed"/> — keep it here so a rule change
    /// cannot miss a page.
    /// </summary>
    public static bool Owns(this ICurrentTenant current, ActivityEvent e) =>
        current.IsHostScope
        || (e.TenantId is null ? e.Kind != ActivityKind.SmtpConnection : e.TenantId == current.TenantId);
}

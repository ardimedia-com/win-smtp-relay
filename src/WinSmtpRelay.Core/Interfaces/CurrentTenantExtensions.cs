using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public static class CurrentTenantExtensions
{
    /// <summary>
    /// Whether an activity event belongs to this scope: untenanted events are visible to everyone,
    /// host scope sees everything, and a tenant scope sees only its own tenant's events. The single
    /// definition for every page that subscribes to <see cref="IActivityFeed"/> — keep it here so a
    /// rule change cannot miss a page.
    /// </summary>
    public static bool Owns(this ICurrentTenant current, ActivityEvent e) =>
        e.TenantId is null || current.IsHostScope || e.TenantId == current.TenantId;
}

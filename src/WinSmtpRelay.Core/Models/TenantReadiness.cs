namespace WinSmtpRelay.Core.Models;

/// <summary>How important a setup item is for a tenant.</summary>
public enum SetupGroup
{
    /// <summary>Must be configured before the tenant can relay mail at all.</summary>
    Required,

    /// <summary>Best practice for deliverability — mail flows without it, but may not reach the inbox.</summary>
    Recommended,

    /// <summary>Optional refinement; a sensible default applies without it.</summary>
    Optional
}

/// <summary>Live state of a single setup item.</summary>
public enum SetupStatus
{
    /// <summary>Fully configured and valid.</summary>
    Done,

    /// <summary>Started but incomplete (e.g. some domains verified, some not).</summary>
    Partial,

    /// <summary>Not configured. For a required item this is a blocker; for an optional item it is fine.</summary>
    Todo,

    /// <summary>Intentionally permissive default applies (e.g. an empty list means "allow all").</summary>
    Permissive,

    /// <summary>A prerequisite item is not yet done, so this one cannot be completed yet.</summary>
    Blocked
}

/// <summary>One checklist item on the Setup &amp; Health page.</summary>
/// <param name="Key">Stable identifier (used for ordering/next-action selection).</param>
/// <param name="Title">Short human-readable title.</param>
/// <param name="Group">Required / Recommended / Optional.</param>
/// <param name="Status">Live status computed from the tenant's data.</param>
/// <param name="Detail">One-line status detail (e.g. "2 of 3 verified").</param>
/// <param name="ActionUrl">Relative URL of the page that configures this item (empty = no link).</param>
/// <param name="ActionLabel">Label for the action link/button.</param>
public sealed record SetupItem(
    string Key,
    string Title,
    SetupGroup Group,
    SetupStatus Status,
    string Detail,
    string ActionUrl,
    string ActionLabel)
{
    /// <summary>True when the item still needs the tenant's attention (Todo or Partial).</summary>
    public bool NeedsAttention => Status is SetupStatus.Todo or SetupStatus.Partial;
}

/// <summary>
/// Aggregated setup readiness for a single tenant. Two distinct bars are exposed:
/// <see cref="CanSend"/> (the hard minimum to relay mail) and the recommended-item count
/// (deliverability best practice). DNS publication is computed separately by the page
/// because it requires a live network lookup.
/// </summary>
/// <param name="TenantId">The tenant this readiness is for, or null in host/all-tenants scope.</param>
/// <param name="TenantName">Display name of the tenant.</param>
/// <param name="TenantActive">Whether the tenant is enabled by the host.</param>
/// <param name="CanSend">True when the tenant meets the minimum to relay mail.</param>
/// <param name="Items">All computed setup items, in display order.</param>
public sealed record TenantReadiness(
    int? TenantId,
    string TenantName,
    bool TenantActive,
    bool CanSend,
    IReadOnlyList<SetupItem> Items)
{
    public IEnumerable<SetupItem> Required => Items.Where(i => i.Group == SetupGroup.Required);
    public IEnumerable<SetupItem> Recommended => Items.Where(i => i.Group == SetupGroup.Recommended);
    public IEnumerable<SetupItem> Optional => Items.Where(i => i.Group == SetupGroup.Optional);

    /// <summary>Recommended items that are fully done (counts toward the deliverability score).</summary>
    public int RecommendedDone => Recommended.Count(i => i.Status == SetupStatus.Done);

    public int RecommendedTotal => Recommended.Count();

    /// <summary>True when this readiness was computed in host/all-tenants scope (no single tenant).</summary>
    public bool IsHostScope => TenantId is null;
}

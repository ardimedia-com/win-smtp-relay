namespace WinSmtpRelay.Core.Models;

/// <summary>
/// A tenant: an isolated set of relay configuration (users, connectors, routing,
/// DKIM, domains, rate limits, queue, statistics) on a shared installation.
/// In phase 0 only admin users and API keys are tenant-bound; business entities
/// gain a TenantId in a later phase.
/// </summary>
public class Tenant
{
    public int Id { get; set; }

    /// <summary>Human-readable tenant name shown in the admin UI.</summary>
    public string Name { get; set; } = "";

    /// <summary>URL/identifier-safe unique slug (lowercase).</summary>
    public string Slug { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public static class TenantDefaults
{
    /// <summary>Id of the seeded default tenant that owns pre-existing single-tenant data.</summary>
    public const int DefaultTenantId = 1;

    public const string DefaultName = "Default";
    public const string DefaultSlug = "default";
}

using WinSmtpRelay.Core.Authorization;

namespace WinSmtpRelay.Core.Models;

/// <summary>
/// A long-lived API key for programmatic access to the admin API (automation, MCP).
/// The plaintext key is shown only once at creation; only a hash is persisted.
/// </summary>
public class ApiKey
{
    public int Id { get; set; }

    /// <summary>Owning tenant. Null for host-level keys.</summary>
    public int? TenantId { get; set; }

    /// <summary>Human-readable label.</summary>
    public string Name { get; set; } = "";

    /// <summary>Public, non-secret prefix used to locate the key for verification and to display it.</summary>
    public string KeyPrefix { get; set; } = "";

    /// <summary>SHA-256 hex hash of the full presented key. API keys are high-entropy, so a fast hash is appropriate (BCrypt is for low-entropy passwords).</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>Role granted to callers using this key. One of <see cref="RelayRoles"/>.</summary>
    public string Role { get; set; } = RelayRoles.TenantViewer;

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresUtc { get; set; }

    public DateTime? LastUsedUtc { get; set; }
}

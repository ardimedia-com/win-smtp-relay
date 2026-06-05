namespace WinSmtpRelay.Core.Models;

public class DkimDomain : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public required string Domain { get; set; }
    public required string Selector { get; set; }

    /// <summary>
    /// The RSA private key in PEM form, stored in the database (generated server-side on create).
    /// This is the preferred source — it keeps key material off the host filesystem so DKIM is safe
    /// per-tenant. Takes precedence over <see cref="PrivateKeyPath"/>.
    /// </summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>
    /// Legacy: an on-disk path to the PEM private key. Retained as a fallback for existing single-tenant
    /// installs that configured a file path; new keys live in <see cref="PrivateKeyPem"/>.
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

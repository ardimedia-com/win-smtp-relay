namespace WinSmtpRelay.Core.Models;

/// <summary>
/// The admin-UI HTTPS certificate imported via the admin UI (single host-level row). When no certificate
/// is imported (<see cref="PfxBase64"/> empty), the service uses the built-in self-signed certificate.
/// The PFX is stored password-less and base64-encoded; it is encrypted at rest (DPAPI) via a value
/// converter, like the DKIM key and smart-host password.
/// </summary>
public class AdminCertificateSettings
{
    public int Id { get; set; }

    /// <summary>The imported certificate as a password-less PFX, base64-encoded. Empty = use the self-signed cert.</summary>
    public string? PfxBase64 { get; set; }

    /// <summary>Certificate subject (display only).</summary>
    public string? Subject { get; set; }

    /// <summary>Certificate SHA-1 thumbprint (display only).</summary>
    public string? Thumbprint { get; set; }

    /// <summary>Certificate expiry (display only).</summary>
    public DateTimeOffset? NotAfterUtc { get; set; }

    /// <summary>When the certificate was imported.</summary>
    public DateTimeOffset? UploadedUtc { get; set; }

    /// <summary>True when a certificate has been imported (otherwise the built-in self-signed cert is used).</summary>
    public bool HasImportedCertificate => !string.IsNullOrEmpty(PfxBase64);
}

using System.Security.Cryptography.X509Certificates;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Manages the admin-UI HTTPS certificate that an operator imports through the admin UI. Persists it
/// (encrypted) in the database and loads it for the HTTPS endpoint. When nothing is imported, the
/// service falls back to the built-in self-signed certificate.
/// </summary>
public interface IAdminCertificateService
{
    /// <summary>Metadata about the currently imported certificate (or an empty record if none).</summary>
    Task<AdminCertificateSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Loads the imported certificate (with private key), or <c>null</c> when none is stored.</summary>
    Task<X509Certificate2?> LoadImportedAsync(CancellationToken ct = default);

    /// <summary>
    /// Validates an uploaded PFX (must contain a private key), stores it password-less and encrypted,
    /// and returns its metadata. Throws when the bytes are not a valid PFX, the password is wrong, or
    /// there is no private key.
    /// </summary>
    Task<AdminCertificateSettings> ImportAsync(byte[] pfxBytes, string? password, CancellationToken ct = default);

    /// <summary>Removes the imported certificate, reverting the admin UI to the built-in self-signed cert.</summary>
    Task ClearAsync(CancellationToken ct = default);
}

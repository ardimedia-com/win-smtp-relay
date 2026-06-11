using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Security;

/// <summary>
/// The single seam that makes a certificate change take effect on the running admin endpoint:
/// persist via <see cref="IAdminCertificateService"/>, then hot-swap the served certificate through
/// <see cref="IAdminCertificateProvider"/> (Kestrel reads it per TLS handshake — no restart needed).
/// Every entry point (admin UI page, a future REST endpoint or ACME renewal) must go through this class:
/// persisting without updating the provider yields an "imported but not served until restart" bug.
/// Scoped lifetime (depends on the scoped certificate service).
/// </summary>
public class AdminCertificateApplier(
    IAdminCertificateService store,
    IAdminCertificateProvider provider,
    IOptions<AdminUiOptions> options,
    ILogger<AdminCertificateApplier> logger)
{
    /// <summary>Imports a PFX (with private key) and serves it immediately.</summary>
    public async Task<AdminCertificateSettings> ImportAndApplyAsync(byte[] pfxBytes, string? password, CancellationToken ct = default)
    {
        var settings = await store.ImportAsync(pfxBytes, password, ct);
        await ApplyImportedAsync(ct);
        return settings;
    }

    /// <summary>Copies a certificate (with private key) from LocalMachine\My and serves it immediately.</summary>
    public async Task<AdminCertificateSettings> ImportFromStoreAndApplyAsync(string thumbprint, CancellationToken ct = default)
    {
        var settings = await store.ImportFromStoreAsync(thumbprint, ct);
        await ApplyImportedAsync(ct);
        return settings;
    }

    /// <summary>Removes the imported certificate and immediately reverts to the configured/self-signed one.</summary>
    public async Task RemoveAndRevertAsync(CancellationToken ct = default)
    {
        await store.ClearAsync(ct);
        var fallback = AdminUiCertificate.Resolve(options.Value, AppContext.BaseDirectory, logger);
        if (fallback is not null)
            provider.Set(fallback);
    }

    private async Task ApplyImportedAsync(CancellationToken ct)
    {
        var cert = await store.LoadImportedAsync(ct);
        if (cert is not null)
            provider.Set(cert);
    }
}

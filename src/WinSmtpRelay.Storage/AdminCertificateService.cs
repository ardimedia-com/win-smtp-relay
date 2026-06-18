using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class AdminCertificateService(RelayDbContext db) : IAdminCertificateService
{
    // Round-trip through a machine key set so SChannel/Kestrel can use the private key. WITHOUT
    // PersistKeySet: the key container then lives only as long as the certificate object (which the cert
    // provider roots for the process lifetime — sufficient for SChannel) instead of accumulating a new
    // orphaned container under MachineKeys on every service start and import.
    private const X509KeyStorageFlags LoadFlags = X509KeyStorageFlags.MachineKeySet;

    public async Task<AdminCertificateSettings> GetAsync(CancellationToken ct = default)
        => await db.AdminCertificateSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct) ?? new AdminCertificateSettings();

    public async Task<X509Certificate2?> LoadImportedAsync(CancellationToken ct = default)
    {
        var row = await db.AdminCertificateSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null || string.IsNullOrEmpty(row.PfxBase64))
            return null;

        var pfx = Convert.FromBase64String(row.PfxBase64);
        return X509CertificateLoader.LoadPkcs12(pfx, null, LoadFlags);
    }

    public async Task<AdminCertificateSettings> ImportAsync(byte[] pfxBytes, string? password, CancellationToken ct = default)
    {
        X509Certificate2 cert;
        try
        {
            cert = X509CertificateLoader.LoadPkcs12(pfxBytes, password,
                LoadFlags | X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The file is not a valid PFX / PKCS#12 certificate, or the password is incorrect.", ex);
        }

        using (cert)
        {
            if (!cert.HasPrivateKey)
                throw new InvalidOperationException(
                    "The certificate has no private key. Export it as a PFX that includes the private key.");

            // Re-export password-less so the user's PFX password is never stored; base64 for the DB column
            // (which is itself DPAPI-encrypted at rest via the value converter).
            var passwordless = cert.Export(X509ContentType.Pfx);

            var row = await db.AdminCertificateSettings.FindAsync([1], ct);
            if (row is null)
            {
                row = new AdminCertificateSettings { Id = 1 };
                db.AdminCertificateSettings.Add(row);
            }

            row.PfxBase64 = Convert.ToBase64String(passwordless);
            row.Subject = cert.Subject;
            row.Thumbprint = cert.Thumbprint;
            row.NotAfterUtc = cert.NotAfter;
            row.UploadedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return await GetAsync(ct);
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        var row = await db.AdminCertificateSettings.FindAsync([1], ct);
        if (row is null)
            return;

        row.PfxBase64 = null;
        row.Subject = null;
        row.Thumbprint = null;
        row.NotAfterUtc = null;
        row.UploadedUtc = null;
        await db.SaveChangesAsync(ct);
    }

    public IReadOnlyList<StoreCertificateInfo> ListStoreCertificates()
    {
        var result = new List<StoreCertificateInfo>();
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        foreach (var cert in store.Certificates)
        {
            using (cert)
            {
                if (cert.HasPrivateKey && HasServerAuthEku(cert))
                    result.Add(new StoreCertificateInfo(
                        cert.Thumbprint, cert.Subject, cert.Issuer, cert.NotAfter, IsTrusted(cert)));
            }
        }
        // Newest expiry first so a freshly-renewed certificate is at the top.
        return result.OrderByDescending(c => c.NotAfterUtc).ToList();
    }

    public async Task<AdminCertificateSettings> ImportFromStoreAsync(string thumbprint, CancellationToken ct = default)
    {
        byte[] pfx;
        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadOnly);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (matches.Count == 0)
                throw new InvalidOperationException("The selected certificate was not found in the LocalMachine\\My store.");

            using var cert = matches[0];
            if (!cert.HasPrivateKey)
                throw new InvalidOperationException("The selected certificate has no private key.");

            try
            {
                // Export with the private key into a password-less PFX, then store it via the normal import
                // path so the relay serves its own self-contained copy (MachineKeySet), independent of the
                // store's private-key ACLs from then on.
                pfx = cert.Export(X509ContentType.Pfx);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not export the certificate's private key. The key must be exportable, and the " +
                    "service account (NetworkService) must have read access to it (certlm.msc → the " +
                    "certificate → All Tasks → Manage Private Keys).", ex);
            }
        }

        return await ImportAsync(pfx, null, ct);
    }

    private static bool HasServerAuthEku(X509Certificate2 cert)
    {
        // A certificate with no EKU is valid for all purposes; one with an EKU must include serverAuth.
        const string serverAuthOid = "1.3.6.1.5.5.7.3.1";
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku is null || eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == serverAuthOid);
    }

    // True when the certificate chains to a root trusted MACHINE-WIDE (LocalMachine\Root) and is currently
    // valid — i.e. a browser on this machine would accept it. We pin the trust anchors to LocalMachine\Root
    // (CustomRootTrust) on purpose: the default Build() also honours the *running account's* per-user root
    // store (CurrentUser\Root), which is non-deterministic (it differs between a dev run and the
    // NetworkService account) and would wrongly accept a cert only the current user trusts. A self-signed
    // cert that is not in the machine root store, or an expired one, returns false — it would still trigger
    // a browser warning, so it is not worth offering.
    private static bool IsTrusted(X509Certificate2 cert)
    {
        try
        {
            using var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            root.Open(OpenFlags.ReadOnly);

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true; // no AIA network fetches
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(root.Certificates);
            return chain.Build(cert);
        }
        catch
        {
            return false;
        }
    }
}

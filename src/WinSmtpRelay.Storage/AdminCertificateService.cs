using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class AdminCertificateService(RelayDbContext db) : IAdminCertificateService
{
    // Keys are loaded through PfxKeyLoader: machine key set first, user key set where the service account may not
    // create a machine key container on this host (the self-signed certificate already falls back the same way;
    // the import did not, so on such a host every import failed). WITHOUT PersistKeySet: the key container then
    // lives only as long as the certificate object (which the cert provider roots for the process lifetime —
    // sufficient for SChannel) instead of accumulating a new orphaned container on every service start and import.

    public async Task<AdminCertificateSettings> GetAsync(CancellationToken ct = default)
        => await db.AdminCertificateSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct) ?? new AdminCertificateSettings();

    public async Task<X509Certificate2?> LoadImportedAsync(CancellationToken ct = default)
    {
        var row = await db.AdminCertificateSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null || string.IsNullOrEmpty(row.PfxBase64))
            return null;

        var pfx = Convert.FromBase64String(row.PfxBase64);
        return PfxKeyLoader.Load(pfx, null);
    }

    public async Task<AdminCertificateSettings> ImportAsync(byte[] pfxBytes, string? password, CancellationToken ct = default)
    {
        X509Certificate2 cert;
        try
        {
            cert = PfxKeyLoader.Load(pfxBytes, password, X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            // Name the underlying cause: the same message also covers a store certificate re-imported by
            // ImportFromStoreAsync, where neither "file" nor "password" is the operator's doing — without the
            // cause there is nothing to act on.
            throw new InvalidOperationException(
                $"The certificate could not be loaded with its private key — not a valid PFX / PKCS#12 file, a wrong password, or the key cannot be imported for the service account. Cause: {ex.GetType().Name}: {ex.Message}", ex);
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
            row.FollowStoreRenewals = false; // an uploaded PFX has no store copy to follow
            await db.SaveChangesAsync(ct);

            return await GetAsync(ct);
        }
    }

    public async Task<string?> FindStoreRenewalAsync(CancellationToken ct = default)
    {
        var row = await db.AdminCertificateSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null || !row.FollowStoreRenewals || string.IsNullOrEmpty(row.PfxBase64))
            return null;

        using var current = PfxKeyLoader.Load(Convert.FromBase64String(row.PfxBase64), null);
        var names = DnsNames(current);
        if (names.Count == 0)
            return null;

        var now = DateTime.Now;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        string? best = null;
        var bestNotAfter = current.NotAfter;
        foreach (var cert in store.Certificates)
        {
            using (cert)
            {
                // A renewal: valid now, later expiry than what is served, private key + serverAuth, trusted
                // machine-wide, and covering every host name the served certificate covers.
                if (cert.NotAfter <= bestNotAfter || cert.NotBefore > now || !cert.HasPrivateKey
                    || !HasServerAuthEku(cert) || !IsTrusted(cert))
                    continue;
                var candidateNames = DnsNames(cert);
                if (!names.All(candidateNames.Contains))
                    continue;
                best = cert.Thumbprint;
                bestNotAfter = cert.NotAfter;
            }
        }
        return best;
    }

    // The certificate's DNS names (SAN dNSName entries; the CN when there is no SAN), lower-case.
    private static HashSet<string> DnsNames(X509Certificate2 cert)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var san in cert.Extensions.OfType<X509SubjectAlternativeNameExtension>())
            foreach (var dns in san.EnumerateDnsNames())
                names.Add(dns.ToLowerInvariant());
        if (names.Count == 0)
        {
            var cn = cert.GetNameInfo(X509NameType.DnsName, forIssuer: false);
            if (!string.IsNullOrWhiteSpace(cn))
                names.Add(cn.ToLowerInvariant());
        }
        return names;
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
        row.FollowStoreRenewals = false;
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

        await ImportAsync(pfx, null, ct);

        // Picked from the store → follow its renewals there (FindStoreRenewalAsync).
        var settings = await db.AdminCertificateSettings.FirstAsync(s => s.Id == 1, ct);
        settings.FollowStoreRenewals = true;
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
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

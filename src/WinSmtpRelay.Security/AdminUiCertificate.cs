using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Configuration;

namespace WinSmtpRelay.Security;

/// <summary>
/// Resolves the certificate for the admin-UI HTTPS endpoint. A configured PFX (AdminUi:CertificatePath)
/// takes priority; otherwise a persistent self-signed certificate is generated on first run and reused.
/// This keeps the management plane on HTTPS out of the box — browsers show a one-time warning for the
/// self-signed certificate, which can later be replaced by importing a real certificate via the admin UI.
/// </summary>
public static class AdminUiCertificate
{
    /// <summary>File name of the generated self-signed certificate, stored in the data directory.</summary>
    public const string SelfSignedFileName = "admin-cert.pfx";

    // The self-signed PFX is a machine-local file protected by the install-directory ACLs; its password
    // is not a secret boundary, so a fixed empty password keeps load/export simple.
    private const string SelfSignedPassword = "";

    // Key-storage strategies tried in order. The relay's service account (NetworkService) may not be able
    // to create a persisted machine key container on every host, so we fall back to a non-persisted
    // machine key set, then the user key set. The first that yields a cert with a usable private key wins.
    // (EphemeralKeySet is intentionally excluded — Windows SChannel/Kestrel can't use ephemeral keys.)
    private static readonly X509KeyStorageFlags[] LoadStrategies =
    [
        X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet,
        X509KeyStorageFlags.MachineKeySet,
        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet,
        X509KeyStorageFlags.UserKeySet,
    ];

    /// <summary>
    /// Returns the certificate to bind the admin HTTPS endpoint to, or <c>null</c> if no certificate
    /// could be prepared (the caller then serves HTTP on loopback / logs a clear error — it must never
    /// crash the service).
    /// </summary>
    public static X509Certificate2? Resolve(AdminUiOptions options, string dataDir, ILogger logger)
    {
        // 1) Explicitly configured PFX takes priority.
        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            if (!File.Exists(options.CertificatePath))
            {
                logger.LogError("Admin UI HTTPS certificate not found at {Path} — falling back.", options.CertificatePath);
                return null;
            }

            var bytes = File.ReadAllBytes(options.CertificatePath);
            var configured = LoadUsable(bytes, options.CertificatePassword, logger);
            if (configured is not null)
                logger.LogInformation("Admin UI HTTPS: using configured certificate {Subject} (expires {Expiry:u}).",
                    configured.Subject, configured.NotAfter);
            else
                logger.LogError("Admin UI HTTPS: configured certificate at {Path} could not be loaded.", options.CertificatePath);
            return configured;
        }

        // 2) Persistent self-signed certificate (generated once, reused across restarts).
        var path = Path.Combine(dataDir, SelfSignedFileName);
        try
        {
            if (File.Exists(path))
            {
                var existing = LoadUsable(File.ReadAllBytes(path), SelfSignedPassword, logger);
                if (existing is not null && existing.NotAfter > DateTimeOffset.UtcNow.AddDays(7))
                {
                    logger.LogInformation("Admin UI HTTPS: using self-signed certificate {Subject} (expires {Expiry:u}).",
                        existing.Subject, existing.NotAfter);
                    return existing;
                }
                existing?.Dispose();
                logger.LogInformation("Admin UI HTTPS: self-signed certificate missing/expiring — regenerating.");
            }

            byte[] pfx;
            using (var generated = CreateSelfSigned())
                pfx = generated.Export(X509ContentType.Pfx, SelfSignedPassword);

            // Persist for reuse across restarts (best-effort — failure here is not fatal, we still load below).
            try { File.WriteAllBytes(path, pfx); }
            catch (Exception ex) { logger.LogWarning(ex, "Admin UI HTTPS: could not persist self-signed certificate to {Path}.", path); }

            var cert = LoadUsable(pfx, SelfSignedPassword, logger);
            if (cert is not null)
                logger.LogWarning(
                    "Admin UI HTTPS: using a generated self-signed certificate. Browsers will show a warning " +
                    "until a trusted certificate is imported via the admin UI.");
            else
                logger.LogError("Admin UI HTTPS: a self-signed certificate was generated but no key-storage strategy could load it.");
            return cert;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin UI HTTPS: could not create or load the self-signed certificate.");
            return null;
        }
    }

    // Loads a PFX trying each key-storage strategy in turn; returns the first cert that exposes a usable
    // private key. Returns null if every strategy fails (each failure is logged for diagnosis).
    private static X509Certificate2? LoadUsable(byte[] pfx, string? password, ILogger logger)
    {
        foreach (var flags in LoadStrategies)
        {
            try
            {
                var cert = X509CertificateLoader.LoadPkcs12(pfx, password, flags);
                if (cert.HasPrivateKey)
                {
                    logger.LogInformation("Admin UI HTTPS: loaded certificate private key with storage flags {Flags}.", flags);
                    return cert;
                }
                cert.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning("Admin UI HTTPS: key-storage strategy {Flags} failed: {Message}", flags, ex.Message);
            }
        }
        return null;
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var hostName = SafeHostName();

        var request = new CertificateRequest(
            $"CN={hostName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        // Enhanced Key Usage: server authentication (1.3.6.1.5.5.7.3.1).
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        if (!string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase))
            san.AddDnsName(hostName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var ip in LocalAddresses())
            san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    private static string SafeHostName()
    {
        try { return Dns.GetHostName(); }
        catch { return "localhost"; }
    }

    // Best-effort enumeration of the host's non-loopback unicast IPs so the self-signed certificate also
    // matches the LAN address when the admin UI is exposed to the network.
    private static IEnumerable<IPAddress> LocalAddresses()
    {
        IPAddress[] addresses;
        try
        {
            addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => !a.IsIPv6LinkLocal && !IPAddress.IsLoopback(a)
                            && a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Distinct()
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var a in addresses)
            yield return a;
    }
}

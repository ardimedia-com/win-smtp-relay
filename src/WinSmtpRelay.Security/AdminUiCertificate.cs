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

    // Round-trip through a machine key set so SChannel/Kestrel can use the private key (an ephemeral CNG
    // key from CreateSelfSigned is not directly usable by the Windows TLS stack).
    private const X509KeyStorageFlags LoadFlags =
        X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet;

    /// <summary>
    /// Returns the certificate to bind the admin HTTPS endpoint to, or <c>null</c> if a configured
    /// certificate path was given but is missing/unreadable (the caller then logs and may fall back).
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

            var configured = X509CertificateLoader.LoadPkcs12FromFile(
                options.CertificatePath, options.CertificatePassword, LoadFlags);
            logger.LogInformation("Admin UI HTTPS: using configured certificate {Subject} (expires {Expiry:u}).",
                configured.Subject, configured.NotAfter);
            return configured;
        }

        // 2) Persistent self-signed certificate (generated once, reused across restarts).
        var path = Path.Combine(dataDir, SelfSignedFileName);
        try
        {
            if (File.Exists(path))
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, SelfSignedPassword, LoadFlags);
                if (existing.NotAfter > DateTimeOffset.UtcNow.AddDays(7))
                {
                    logger.LogInformation("Admin UI HTTPS: using self-signed certificate {Subject} (expires {Expiry:u}).",
                        existing.Subject, existing.NotAfter);
                    return existing;
                }
                logger.LogInformation("Admin UI HTTPS: self-signed certificate is expiring — regenerating.");
            }

            using (var generated = CreateSelfSigned())
                File.WriteAllBytes(path, generated.Export(X509ContentType.Pfx, SelfSignedPassword));

            logger.LogWarning(
                "Admin UI HTTPS: generated a self-signed certificate at {Path}. Browsers will show a " +
                "certificate warning until a trusted certificate is imported via the admin UI.", path);

            return X509CertificateLoader.LoadPkcs12FromFile(path, SelfSignedPassword, LoadFlags);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin UI HTTPS: could not create or load the self-signed certificate.");
            return null;
        }
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

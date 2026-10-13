using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WinSmtpRelay.Core;

/// <summary>
/// Loads a PFX so that its private key is usable by Windows SChannel (Kestrel's HTTPS): first into the machine key
/// set, then — where the service account (NetworkService) may not create a machine key container on this host —
/// into the user key set. Never ephemeral: SChannel cannot use ephemeral keys. Shared by the self-signed admin
/// certificate and the imported one, so both behave the same on every host.
/// </summary>
public static class PfxKeyLoader
{
    /// <summary>The key-storage strategies, in the order they are tried.</summary>
    public static readonly X509KeyStorageFlags[] Strategies =
    [
        X509KeyStorageFlags.MachineKeySet,
        X509KeyStorageFlags.UserKeySet,
    ];

    /// <summary>
    /// Loads <paramref name="pfx"/> with the first strategy that yields a certificate with a private key. Throws a
    /// <see cref="CryptographicException"/> naming every strategy's failure when none works (a wrong password
    /// fails every strategy alike).
    /// </summary>
    public static X509Certificate2 Load(byte[] pfx, string? password, X509KeyStorageFlags additionalFlags = X509KeyStorageFlags.DefaultKeySet)
    {
        var failures = new List<string>();
        foreach (var strategy in Strategies)
        {
            try
            {
                var cert = X509CertificateLoader.LoadPkcs12(pfx, password, strategy | additionalFlags);
                if (cert.HasPrivateKey)
                    return cert;
                cert.Dispose();
                failures.Add($"{strategy}: no private key");
            }
            catch (CryptographicException ex)
            {
                failures.Add($"{strategy}: {ex.Message}");
            }
        }

        throw new CryptographicException(string.Join(" | ", failures));
    }
}

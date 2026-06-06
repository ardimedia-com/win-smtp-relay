using System.Security.Cryptography;
using System.Text;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Protects DKIM RSA private keys at rest using Windows DPAPI
/// (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.LocalMachine"/>), so the key material
/// is not stored as plaintext PEM in the SQLite database. Applied transparently via an EF value
/// converter on <c>DkimDomain.PrivateKeyPem</c>.
///
/// Encrypted values are stored as <c>dpapi:&lt;base64&gt;</c>. The marker lets reads distinguish
/// encrypted values from pre-existing plaintext rows: legacy plaintext (no marker, or anything that
/// fails to decrypt) is returned unchanged and gets re-encrypted on the next save (lazy migration).
///
/// DPAPI is Windows-only; the application host targets net10.0-windows, so this is always available
/// at runtime. The package supplies the API surface on net10.0, but the calls throw
/// <see cref="PlatformNotSupportedException"/> on non-Windows — acceptable because the relay only runs
/// on Windows.
/// </summary>
public static class DkimKeyProtector
{
    /// <summary>Marker prefix on stored values that have been DPAPI-encrypted.</summary>
    private const string Marker = "dpapi:";

    /// <summary>
    /// Fixed application entropy mixed into the DPAPI ciphertext. Not a secret (it ships in the binary),
    /// but it scopes the protection to this application so unrelated LocalMachine-protected blobs can't
    /// be cross-decrypted.
    /// </summary>
    private static readonly byte[] Entropy = "WinSmtpRelay.Dkim.PrivateKey.v1"u8.ToArray();

    /// <summary>
    /// Encrypts a plaintext PEM private key for storage. Null/empty is returned unchanged.
    /// The result is <c>dpapi:&lt;base64&gt;</c>.
    /// </summary>
    public static string? Protect(string? plaintext)
    {
        // DPAPI is Windows-only. The relay only runs on Windows; on any other host (e.g. a Linux CI
        // box) fall back to storing the value as-is rather than crashing.
        if (string.IsNullOrEmpty(plaintext) || !OperatingSystem.IsWindows())
            return plaintext;

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine);
        return Marker + Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// Decrypts a stored value back to the plaintext PEM. Null/empty is returned unchanged. Values
    /// without the <c>dpapi:</c> marker — or any value that fails to decrypt — are returned unchanged
    /// (graceful lazy migration of pre-existing plaintext rows, which get re-encrypted on next save).
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)
            || !stored.StartsWith(Marker, StringComparison.Ordinal)
            || !OperatingSystem.IsWindows())
            return stored;

        try
        {
            var protectedBytes = Convert.FromBase64String(stored[Marker.Length..]);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // Corrupt/undecryptable value: fail open to the stored text rather than losing the key.
            return stored;
        }
    }
}

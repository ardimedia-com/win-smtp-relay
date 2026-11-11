using System.Security.Cryptography;
using System.Text;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Protects secrets at rest using Windows DPAPI
/// (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.LocalMachine"/>), so credential
/// material is not stored as plaintext in the SQLite database. Applied transparently via EF value
/// converters — currently on <c>DkimDomain.PrivateKeyPem</c> (the RSA signing key) and
/// <c>SendConnector.EncryptedPassword</c> (the upstream smart-host / submission password, e.g. a Brevo
/// SMTP key). Callers read and write decrypted plaintext; encryption happens only at the storage edge.
///
/// Encrypted values are stored as <c>dpapi:&lt;base64&gt;</c>. The marker lets reads distinguish
/// encrypted values from pre-existing plaintext rows: legacy plaintext (no marker) is returned unchanged and gets
/// re-encrypted on the next save (lazy migration).
///
/// A value WITH the marker that cannot be decrypted — typically a database restored onto another machine, whose
/// DPAPI machine key differs — fails closed: <see cref="Unprotect"/> throws <see cref="SecretUnavailableException"/>.
/// The EF converter cannot throw without making the whole row unreadable (an admin could then not even re-enter
/// the secret), so it keeps the undecryptable ciphertext as the property value; every consumer checks it with
/// <see cref="RequireUsable"/> before use, the self-check reports it, and <see cref="Protect"/> never wraps it a
/// second time on save.
///
/// DPAPI is Windows-only; the application host targets net10.0-windows, so this is always available
/// at runtime. The package supplies the API surface on net10.0, but the calls throw
/// <see cref="PlatformNotSupportedException"/> on non-Windows — acceptable because the relay only runs
/// on Windows.
/// </summary>
public static class SecretProtector
{
    /// <summary>Marker prefix on stored values that have been DPAPI-encrypted.</summary>
    private const string Marker = "dpapi:";

    /// <summary>
    /// Fixed application entropy mixed into the DPAPI ciphertext. Not a secret (it ships in the binary),
    /// but it scopes the protection to this application so unrelated LocalMachine-protected blobs can't
    /// be cross-decrypted. The literal value is HISTORICAL and must not change: existing rows (DKIM keys
    /// first protected under this name) were encrypted with these exact bytes, and altering them would
    /// make those rows undecryptable.
    /// </summary>
    private static readonly byte[] Entropy = "WinSmtpRelay.Dkim.PrivateKey.v1"u8.ToArray();

    /// <summary>
    /// Encrypts a plaintext secret for storage. Null/empty is returned unchanged. A value that already carries
    /// the marker (an undecryptable ciphertext kept by the converter) is returned unchanged too, so saving a row
    /// never wraps ciphertext in a second layer. The result is <c>dpapi:&lt;base64&gt;</c>.
    /// </summary>
    public static string? Protect(string? plaintext)
    {
        // DPAPI is Windows-only. The relay only runs on Windows; on any other host (e.g. a Linux CI
        // box) fall back to storing the value as-is rather than crashing.
        if (string.IsNullOrEmpty(plaintext) || !OperatingSystem.IsWindows())
            return plaintext;
        if (IsProtected(plaintext))
            return plaintext;

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine);
        return Marker + Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// Decrypts a stored value back to plaintext. Null/empty and legacy plaintext (no marker) are returned
    /// unchanged. Throws <see cref="SecretUnavailableException"/> when a protected value cannot be decrypted —
    /// never hands the ciphertext back as if it were the secret.
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)
            || !IsProtected(stored)
            || !OperatingSystem.IsWindows())
            return stored;

        try
        {
            var protectedBytes = Convert.FromBase64String(stored[Marker.Length..]);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new SecretUnavailableException(
                "A stored secret could not be decrypted on this machine (DPAPI). This happens when the database was "
                + "restored from another machine. Enter the secret again.", ex);
        }
    }

    /// <summary>
    /// The EF read converter: like <see cref="Unprotect"/>, but keeps an undecryptable value as it is (still
    /// marked) instead of throwing, so the row stays readable and editable. Consumers must pass the property
    /// through <see cref="RequireUsable"/> before using it.
    /// </summary>
    public static string? UnprotectOrKeep(string? stored)
    {
        try
        {
            return Unprotect(stored);
        }
        catch (SecretUnavailableException)
        {
            return stored;
        }
    }

    /// <summary>
    /// True when a value read through the converter is still ciphertext — the secret is not usable on this
    /// machine. (A decrypted plaintext never carries the marker.)
    /// </summary>
    public static bool IsUnavailable(string? value) =>
        !string.IsNullOrEmpty(value) && IsProtected(value) && OperatingSystem.IsWindows();

    /// <summary>
    /// Returns <paramref name="value"/> when it is a usable secret; throws <see cref="SecretUnavailableException"/>
    /// naming <paramref name="what"/> when it is undecryptable ciphertext. Call before using a secret read from
    /// the database (smart-host password, DKIM key, certificate).
    /// </summary>
    public static string? RequireUsable(string? value, string what) =>
        IsUnavailable(value)
            ? throw new SecretUnavailableException(
                $"The {what} could not be decrypted on this machine (DPAPI) — the database was probably restored "
                + "from another machine. Enter it again in the admin UI.")
            : value;

    private static bool IsProtected(string value) => value.StartsWith(Marker, StringComparison.Ordinal);
}

/// <summary>
/// A stored secret exists but cannot be decrypted on this machine. Never fall back to the ciphertext: the caller
/// must fail (a temporary delivery failure, a self-check error) until the secret is entered again.
/// </summary>
public sealed class SecretUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

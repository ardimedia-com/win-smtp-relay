using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage.Identity;

/// <summary>
/// The one-time first-run setup token. While no administrator exists, whoever reaches the admin port could
/// otherwise claim the relay through <c>/account/initial-setup</c> — also right after the installer's «reset
/// administrator access». The token proves the claimant can read a file only Administrators and SYSTEM can read,
/// i.e. that they administer the machine. Only its hash is kept in memory; it is re-issued on every service start
/// while no administrator exists and dropped once the first one is created. Process-wide singleton.
/// </summary>
public sealed class InitialSetupToken(bool grantCurrentUserRead = false)
{
    public const string FileName = "setup-token.txt";

    private readonly Lock _gate = new();
    private byte[]? _hash;

    /// <summary>Where the current token was written; null while none is issued.</summary>
    public string? FilePath { get; private set; }

    public bool IsIssued
    {
        get { lock (_gate) return _hash is not null; }
    }

    /// <summary>
    /// Generates a fresh token, writes it to <paramref name="directory"/> (replacing an older one) readable only
    /// by Administrators and SYSTEM, and returns the file path. The token itself is never logged.
    /// </summary>
    public string Issue(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var token = NewToken();
        var content = "WIN-SMTP-RELAY first-run setup token. Enter it on /account/initial-setup to create the first "
            + "administrator. It is valid until the first administrator exists, and replaced on every service start "
            + "until then." + Environment.NewLine + Environment.NewLine + token + Environment.NewLine;

        lock (_gate)
        {
            if (File.Exists(path))
                File.Delete(path);
            WriteRestricted(path, content);
            _hash = Hash(token);
            FilePath = path;
        }
        return path;
    }

    /// <summary>Constant-time check of a presented token (dashes, spaces and case are ignored).</summary>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented))
            return false;
        lock (_gate)
            return _hash is not null && CryptographicOperations.FixedTimeEquals(_hash, Hash(presented));
    }

    /// <summary>Invalidates the token and removes its file (best effort).</summary>
    public void Consume()
    {
        lock (_gate)
        {
            _hash = null;
            var path = FilePath;
            FilePath = null;
            if (path is not null)
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string NewToken()
    {
        // 120 bits as 24 Crockford-style base32 characters in groups of four — typeable, no ambiguous characters.
        const string alphabet = "ABCDEFGHJKMNPQRSTVWXYZ0123456789";
        var chars = new char[24];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return string.Join('-', Enumerable.Range(0, 6).Select(g => new string(chars, g * 4, 4)));
    }

    private static byte[] Hash(string token)
    {
        var normalized = new string(token.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
    }

    private void WriteRestricted(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (!OperatingSystem.IsWindows())
        {
            using var unixStream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            unixStream.Write(bytes);
            return;
        }

        // No inherited rules: Administrators and SYSTEM may read; the service's own account may only delete the
        // file (to replace it on the next start) — it never needs to read it back, the hash is kept in memory. The
        // handle opened at creation may write regardless of the new DACL.
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } self)
        {
            var rights = grantCurrentUserRead ? FileSystemRights.Read | FileSystemRights.Delete : FileSystemRights.Delete;
            security.AddAccessRule(new FileSystemAccessRule(self, rights, AccessControlType.Allow));
        }

        using var stream = new FileInfo(path).Create(
            FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
        stream.Write(bytes);
    }
}

/// <summary>Outcome of <see cref="IInitialAdminSetup.CreateFirstAdminAsync"/>.</summary>
public sealed record InitialSetupResult(bool Succeeded, AdminUser? User = null, string? Error = null, bool AlreadyConfigured = false);

/// <summary>First-run setup: issues the setup token and creates the first administrator atomically.</summary>
public interface IInitialAdminSetup
{
    /// <summary>True while no administrator account exists.</summary>
    Task<bool> IsRequiredAsync(CancellationToken ct = default);

    /// <summary>
    /// While setup is required, makes sure a token is issued (writes the file on first call) and returns the file
    /// path; null once an administrator exists.
    /// </summary>
    Task<string?> EnsureTokenIssuedAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates the first host administrator (host membership + default-tenant admin membership) when the token
    /// matches and still no administrator exists. Serialised process-wide and re-checked inside a transaction, so
    /// two concurrent claims cannot both succeed.
    /// </summary>
    Task<InitialSetupResult> CreateFirstAdminAsync(string token, string email, string? displayName, string password,
        CancellationToken ct = default);
}

public class InitialAdminSetup(
    RelayDbContext db,
    UserManager<AdminUser> userManager,
    IAdminMembershipService memberships,
    IAdminAuditService audit,
    InitialSetupToken token,
    ILogger<InitialAdminSetup> logger) : IInitialAdminSetup
{
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    public async Task<bool> IsRequiredAsync(CancellationToken ct = default)
        => !await db.Users.AnyAsync(ct);

    public async Task<string?> EnsureTokenIssuedAsync(CancellationToken ct = default)
    {
        if (!await IsRequiredAsync(ct))
            return null;
        if (token.IsIssued && token.FilePath is { } existing && File.Exists(existing))
            return existing;

        var path = token.Issue(DataDirectory());
        // Event Log hint (the service logs there): WHERE the token is — never the token itself.
        logger.LogWarning(
            "No administrator exists. A one-time setup token was written to {TokenPath} (readable by Administrators "
            + "only). Open /account/initial-setup and enter it to create the first administrator.", path);
        return path;
    }

    public async Task<InitialSetupResult> CreateFirstAdminAsync(string presentedToken, string email, string? displayName,
        string password, CancellationToken ct = default)
    {
        await CreateGate.WaitAsync(ct);
        try
        {
            if (!await IsRequiredAsync(ct))
                return new InitialSetupResult(false, AlreadyConfigured: true);

            if (!token.Matches(presentedToken))
            {
                logger.LogWarning("First-run setup refused: wrong or missing setup token");
                return new InitialSetupResult(false, Error: "The setup token is not correct. Copy it from the token file on the server.");
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Re-check inside the transaction: the gate covers this process, the transaction the database.
            if (await db.Users.AnyAsync(ct))
                return new InitialSetupResult(false, AlreadyConfigured: true);

            var user = new AdminUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Administrator" : displayName.Trim(),
                MustChangePassword = false,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            var created = await userManager.CreateAsync(user, password);
            if (!created.Succeeded)
                return new InitialSetupResult(false, Error: string.Join(" ", created.Errors.Select(e => e.Description)));

            // Host membership (full host administration) AND tenant-admin of the default tenant — the bootstrap
            // exception, so the initial tenant has an admin without anyone having to grant it.
            await memberships.GrantAsync(user.Id, null, RelayRoles.HostAdmin, grantedByUserId: user.Id, ct: ct);
            await memberships.GrantAsync(user.Id, TenantDefaults.DefaultTenantId, RelayRoles.TenantAdmin, grantedByUserId: user.Id, ct: ct);
            await audit.WriteAsync(AdminAuditActions.AdminCreated, user.Id, user.Email, targetUserId: user.Id,
                detail: "first-run host administrator (host + default-tenant membership, setup token)", ct: ct);
            await tx.CommitAsync(ct);

            token.Consume();
            logger.LogWarning("First-run setup created the initial host administrator {Email}", email);
            return new InitialSetupResult(true, user);
        }
        finally
        {
            CreateGate.Release();
        }
    }

    /// <summary>The folder holding the SQLite database — the relay's data folder, wherever the host put it.</summary>
    private string DataDirectory()
    {
        var source = db.Database.GetDbConnection().DataSource;
        if (!string.IsNullOrWhiteSpace(source) && !source.StartsWith(':') &&
            Path.GetDirectoryName(Path.GetFullPath(source)) is { Length: > 0 } dir)
            return dir;
        // In-memory database (tests): fall back to the working directory, which the service host sets to the data
        // directory — never the program folder, which is read-only for the service account (T1076).
        return Directory.GetCurrentDirectory();
    }
}

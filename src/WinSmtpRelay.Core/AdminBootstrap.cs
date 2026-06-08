namespace WinSmtpRelay.Core;

/// <summary>Shared constants for the initial-admin bootstrap (seeded account + one-time password file).</summary>
public static class AdminBootstrap
{
    /// <summary>Username/email of the seeded initial host administrator.</summary>
    public const string InitialAdminEmail = "admin@local";

    /// <summary>
    /// File written next to the service binaries holding the one-time initial admin password. It is
    /// surfaced (path/link) on the sign-in page and deleted automatically once the password is changed.
    /// </summary>
    public const string PasswordFileName = "initial-admin-password.txt";
}

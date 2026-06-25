namespace WinSmtpRelay.Core;

/// <summary>Shared constants for administrator bootstrap.</summary>
public static class AdminBootstrap
{
    /// <summary>
    /// Username/email of the legacy seeded host administrator. No longer created on install; retained only
    /// so startup maintenance can retire a leftover <c>admin@local</c> from older installs, after which the
    /// first administrator is defined through first-run setup (<c>/account/initial-setup</c>).
    /// </summary>
    public const string InitialAdminEmail = "admin@local";
}

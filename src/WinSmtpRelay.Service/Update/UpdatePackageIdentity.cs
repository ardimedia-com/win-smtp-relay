namespace WinSmtpRelay.Service.Update;

/// <summary>
/// What a self-update package must be (decision T1077): WinSmtpRelay's MSI (its UpgradeCode and product name),
/// signed by the pinned publisher, and strictly newer than the installed version. The same values are baked into
/// the SYSTEM updater script (<c>setup/WinSmtpRelay.Setup/updater/Update-WinSmtpRelay.ps1</c>), which re-checks
/// everything independently before it installs anything — keep both in sync.
/// <para>
/// The values are compiled in on purpose, not read from configuration: the configuration lives in the data
/// directory, which the service account can write.
/// </para>
/// </summary>
public static class UpdatePackageIdentity
{
    /// <summary>The MSI UpgradeCode of every WinSmtpRelay release (Package.wxs).</summary>
    public const string UpgradeCode = "{2CBB38D8-5035-40DB-B30B-0E55F24EC496}";

    /// <summary>Every release's ProductName is "WinSmtpRelay" or "WinSmtpRelay &lt;version label&gt;".</summary>
    public const string ProductName = "WinSmtpRelay";

    /// <summary>
    /// Organization (O=) of the code-signing certificate. Releases are signed in CI with the ARDIMEDIA code-signing
    /// certificate "CN=ARDIMEDIA, O=ARDIMEDIA, C=LI" (decision T1084 D).
    /// </summary>
    public const string SignerOrganization = "ARDIMEDIA";

    /// <summary>
    /// SHA-1 thumbprints of the certificates a release may be signed with — checked IN ADDITION to the O= pin and to
    /// WinVerifyTrust (the chain must still be trusted on the host). A list so a certificate rollover can carry the old
    /// and the new thumbprint for one release: ship a release that lists both (still signed with the old certificate),
    /// then sign the following releases with the new one, then drop the old thumbprint. The SYSTEM updater script has
    /// its own copy of this list, which is the one that protects the installation.
    /// Current: CN=ARDIMEDIA, O=ARDIMEDIA, C=LI, valid 2026-06-26 to 2031-06-26.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSignerThumbprints = ["11E81500C908EC01E783ACBBB2E00ABAAA603AC3"];

    /// <summary>True when <paramref name="thumbprint"/> is one of <paramref name="pinned"/> (case and spaces ignored).</summary>
    public static bool IsPinnedThumbprint(string? thumbprint, IEnumerable<string> pinned)
    {
        static string Normalize(string? t) => new string((t ?? "").Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        var candidate = Normalize(thumbprint);
        return candidate.Length == 40 && pinned.Any(p => Normalize(p) == candidate);
    }

    /// <summary>True when the MSI's UpgradeCode and ProductName are WinSmtpRelay's.</summary>
    public static bool IsOurProduct(string? upgradeCode, string? productName, out string? reason)
    {
        reason = null;
        if (!Guid.TryParse(upgradeCode, out var code) || code != Guid.Parse(UpgradeCode))
        {
            reason = $"the package's UpgradeCode ({upgradeCode ?? "none"}) is not WinSmtpRelay's";
            return false;
        }
        if (productName is null ||
            !(productName.Equals(ProductName, StringComparison.Ordinal) ||
              productName.StartsWith(ProductName + " ", StringComparison.Ordinal)))
        {
            reason = $"the package's ProductName ({productName ?? "none"}) is not WinSmtpRelay";
            return false;
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a strictly greater Windows Installer version than
    /// <paramref name="installed"/>. Windows Installer compares only major.minor.build, so a fourth field is ignored —
    /// exactly as a MajorUpgrade would. An unparsable candidate is never newer.
    /// </summary>
    public static bool IsNewer(string? candidate, string? installed, out string? reason)
    {
        reason = null;
        if (!TryParseMsiVersion(candidate, out var c))
        {
            reason = $"the package's ProductVersion ({candidate ?? "none"}) is not a valid Windows Installer version";
            return false;
        }
        if (!TryParseMsiVersion(installed, out var i))
            return true; // nothing (readable) installed: any valid version is an upgrade

        if (c.CompareTo(i) > 0)
            return true;
        reason = $"the package's version {c} is not newer than the installed version {i}";
        return false;
    }

    /// <summary>Parses "major.minor.build[.revision]" into a three-field version (major/minor &lt;= 255, build &lt;= 65535).</summary>
    public static bool TryParseMsiVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split('.');
        if (parts.Length < 2 || parts.Length > 4)
            return false;
        var fields = new int[3];
        for (int n = 0; n < Math.Min(parts.Length, 3); n++)
        {
            if (!int.TryParse(parts[n], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out fields[n]))
                return false;
        }
        if (parts.Length == 4 && !int.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            return false;
        if (fields[0] > 255 || fields[1] > 255 || fields[2] > 65535)
            return false;
        version = new Version(fields[0], fields[1], fields[2]);
        return true;
    }
}

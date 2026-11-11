using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace WinSmtpRelay.Service.Update;

/// <summary>
/// Reads an MSI package's identity (UpgradeCode, ProductName, ProductVersion) from its Property table and the
/// version of the installed WinSmtpRelay product, through the Windows Installer API (msi.dll).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MsiPackageInspector
{
    public sealed record PackageInfo(string? UpgradeCode, string? ProductName, string? ProductVersion);

    /// <summary>Reads the package's Property table. Throws when the file is not a readable MSI database.</summary>
    public static PackageInfo ReadPackage(string msiPath)
    {
        uint rc = MsiOpenDatabaseW(msiPath, (IntPtr)MSIDBOPEN_READONLY, out var db);
        if (rc != 0)
            throw new InvalidOperationException($"The file is not a readable Windows Installer package (error {rc}).");
        try
        {
            return new PackageInfo(
                ReadProperty(db, "UpgradeCode"),
                ReadProperty(db, "ProductName"),
                ReadProperty(db, "ProductVersion"));
        }
        finally
        {
            MsiCloseHandle(db);
        }
    }

    /// <summary>
    /// The highest installed version of any product with <paramref name="upgradeCode"/>, or null when none is
    /// installed (or the Windows Installer cannot tell).
    /// </summary>
    public static string? InstalledVersion(string upgradeCode)
    {
        Version? best = null;
        string? bestText = null;
        for (uint index = 0; ; index++)
        {
            var productCode = new StringBuilder(39);
            uint rc = MsiEnumRelatedProductsW(upgradeCode, 0, index, productCode);
            if (rc != 0) // ERROR_NO_MORE_ITEMS (259) or an error: stop
                break;

            uint length = 64;
            var value = new StringBuilder((int)length);
            if (MsiGetProductInfoW(productCode.ToString(), "VersionString", value, ref length) != 0)
                continue;
            var text = value.ToString();
            if (UpdatePackageIdentity.TryParseMsiVersion(text, out var v) && (best is null || v > best))
            {
                best = v;
                bestText = text;
            }
        }
        return bestText;
    }

    private static string? ReadProperty(IntPtr db, string name)
    {
        // The property name is a fixed identifier from this class, never input — no SQL injection surface.
        uint rc = MsiDatabaseOpenViewW(db, $"SELECT `Value` FROM `Property` WHERE `Property`='{name}'", out var view);
        if (rc != 0)
            return null;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0)
                return null;
            if (MsiViewFetch(view, out var record) != 0)
                return null;
            try
            {
                uint length = 0;
                var empty = new StringBuilder(1);
                rc = MsiRecordGetStringW(record, 1, empty, ref length); // ERROR_MORE_DATA: length = required size
                if (rc != 0 && rc != ERROR_MORE_DATA)
                    return null;
                length++;
                var value = new StringBuilder((int)length);
                return MsiRecordGetStringW(record, 1, value, ref length) == 0 ? value.ToString() : null;
            }
            finally
            {
                MsiCloseHandle(record);
            }
        }
        finally
        {
            MsiViewClose(view);
            MsiCloseHandle(view);
        }
    }

    private const int MSIDBOPEN_READONLY = 0;
    private const uint ERROR_MORE_DATA = 234;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiOpenDatabaseW(string szDatabasePath, IntPtr szPersist, out IntPtr phDatabase);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiDatabaseOpenViewW(IntPtr hDatabase, string szQuery, out IntPtr phView);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewExecute(IntPtr hView, IntPtr hRecord);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewFetch(IntPtr hView, out IntPtr phRecord);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewClose(IntPtr hView);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiRecordGetStringW(IntPtr hRecord, uint iField, StringBuilder szValueBuf, ref uint pcchValueBuf);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(IntPtr hAny);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumRelatedProductsW(string lpUpgradeCode, uint dwReserved, uint iProductIndex, StringBuilder lpProductBuf);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoW(string szProduct, string szProperty, StringBuilder lpValueBuf, ref uint pcchValueBuf);
}

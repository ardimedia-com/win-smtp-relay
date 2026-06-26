using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace WinSmtpRelay.Service.Update;

/// <summary>
/// Verifies an MSI's Authenticode signature via WinVerifyTrust (signature present + valid + chain trusted)
/// and pins the publisher (the signer subject must contain the expected organisation). This is the
/// service-side pre-check; the elevated SYSTEM updater task re-verifies before installing, so this account
/// being compromised cannot get an unsigned/foreign package installed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AuthenticodeVerifier
{
    public static bool Verify(string filePath, string expectedPublisher, out string? signerSubject, out string? error)
    {
        signerSubject = null;
        error = null;

        // 1) WinVerifyTrust: the file carries a valid Authenticode signature chaining to a trusted root.
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                // Skip online revocation so a momentarily offline CRL/OCSP can't block a legitimate update;
                // chain-trust + publisher pinning remain enforced.
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
            };

            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            if (result != 0)
            {
                error = $"The installer's Authenticode signature is not valid or not trusted (0x{result:X8}).";
                return false;
            }
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }

        // 2) Publisher pinning: the signer's Organization (O) RDN must EQUAL the expected org exactly — not a
        //    loose subject substring — so a foreign certificate whose subject merely contains the string
        //    (e.g. O=ARDIMEDIA-SOMETHING) cannot pass.
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the supported way to read the Authenticode signer
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            signerSubject = cert.Subject;
            if (!HasOrganization(cert, expectedPublisher))
            {
                error = $"The installer is signed, but its Organization (O) is not '{expectedPublisher}' (signer: {signerSubject}).";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"Could not read the installer's signer certificate: {ex.Message}";
            return false;
        }

        return true;
    }

    // True if the certificate subject has an Organization (O, OID 2.5.4.10) RDN whose value equals
    // expectedOrg (case-insensitive). Pins on the O field rather than a subject substring.
    private static bool HasOrganization(X509Certificate2 cert, string expectedOrg)
    {
        const string organizationOid = "2.5.4.10";
        foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            try
            {
                if (rdn.GetSingleElementType().Value == organizationOid &&
                    string.Equals(rdn.GetSingleElementValue(), expectedOrg, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (InvalidOperationException)
            {
                // A multi-valued RDN — not our single-valued O; skip it.
            }
        }
        return false;
    }

    // ---- WinVerifyTrust interop ----

    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile; // union: we use the file-info pointer
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}

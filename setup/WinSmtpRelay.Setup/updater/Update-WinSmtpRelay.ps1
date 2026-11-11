# WIN-SMTP-RELAY unattended updater - runs as SYSTEM from the scheduled task "WinSmtpRelayUpdate".
#
# Windows PowerShell 5.1, ASCII only. This file is NOT installed anywhere: the installer's custom action
# (RegisterUpdateTask) embeds it, encodes it into the task's -EncodedCommand and registers the task as SYSTEM.
# The task definition lives under C:\Windows\System32\Tasks, which only SYSTEM and Administrators can change,
# so the low-privilege service account (NetworkService) cannot alter what runs here. Full-line comments and
# blank lines are stripped before encoding.
#
# This script is the security boundary of the self-update (decision T1077). The relay service downloads and
# pre-checks the MSI, stages it as %ProgramData%\Ardimedia\WinSmtpRelay\updates\pending.msi and writes Application
# event 9100 (source WinSmtpRelay.Service), which triggers the task. Everything is re-checked here, on a copy in
# the SYSTEM-only folder updater-work (no time-of-check/time-of-use gap):
#   1. Authenticode signature valid, chain trusted, NOT revoked (online check over the whole chain; fails closed)
#   2. the signer's Organization (O=) is exactly the pinned publisher AND the signer certificate's thumbprint is one
#      of the pinned thumbprints (decision T1084 D). Certificate rollover: ship one release (signed with the OLD
#      certificate) whose list carries old + new thumbprint, sign later releases with the new certificate, then drop
#      the old thumbprint. This list is compiled into the task; it is deliberately NOT read from the configuration,
#      which the service account can write.
#   3. the MSI's UpgradeCode and ProductName are WinSmtpRelay's
#   4. the MSI's ProductVersion is strictly greater than the installed WinSmtpRelay version
# Only then msiexec installs it. Keep the pins in sync with src/WinSmtpRelay.Service/Update/UpdatePackageIdentity.cs.
#
# Exit codes: 0 installed, 10 nothing staged, 11 signature/revocation, 12 publisher, 13 not WinSmtpRelay,
# 14 not newer, 15 unreadable package, 16 certificate thumbprint not pinned, other = msiexec exit code. Results go to updater-work\update.log and to the
# Application event log (source WinSmtpRelay.Service, event 9101 information / 9102 error).

$ErrorActionPreference = 'Stop'
$UpgradeCode = '{2CBB38D8-5035-40DB-B30B-0E55F24EC496}'
$ProductName = 'WinSmtpRelay'
$Organization = 'ARDIMEDIA'
# CN=ARDIMEDIA, O=ARDIMEDIA, C=LI - valid 2026-06-26 to 2031-06-26
$SignerThumbprints = @('11E81500C908EC01E783ACBBB2E00ABAAA603AC3')
$EventSource = 'WinSmtpRelay.Service'

$base = Join-Path $env:ProgramData 'Ardimedia\WinSmtpRelay'
$pending = Join-Path $base 'updates\pending.msi'
$work = Join-Path $base 'updater-work'
$staged = [string](Join-Path $work 'staged.msi')
$log = Join-Path $work 'update.log'
$msiLog = Join-Path $work 'msiexec.log'

function Write-UpdateLog([string]$Message, [int]$EventId = 9101, [string]$Type = 'Information') {
    $line = (Get-Date).ToUniversalTime().ToString('o') + ' ' + $Message
    Add-Content -LiteralPath $log -Value $line -Encoding ASCII
    try { Write-EventLog -LogName Application -Source $EventSource -EventId $EventId -EntryType $Type -Message $Message } catch { }
}

function Stop-Update([int]$Code, [string]$Reason) {
    Write-UpdateLog ('Update NOT installed: ' + $Reason) 9102 'Error'
    Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
    exit $Code
}

function ConvertTo-MsiVersion([string]$Text) {
    # Windows Installer compares major.minor.build only.
    if ($Text -notmatch '^\d+\.\d+(\.\d+){0,2}$') { return $null }
    $p = $Text.Split('.')
    $build = 0
    if ($p.Length -ge 3) { $build = [int]$p[2] }
    return New-Object -TypeName System.Version -ArgumentList ([int]$p[0]), ([int]$p[1]), $build
}

function Invoke-Com($Object, [string]$Member, [string]$Kind, $Arguments) {
    # Cmdlet output (Join-Path, ...) arrives wrapped in a PSObject, which IDispatch rejects with DISP_E_TYPEMISMATCH
    # (build80 could not read any package for that reason). Hand COM the plain .NET values.
    if ($null -ne $Arguments) {
        $Arguments = [object[]]@($Arguments | ForEach-Object { if ($null -eq $_) { $null } else { $_.PSObject.BaseObject } })
    }
    return $Object.GetType().InvokeMember($Member, $Kind, $null, $Object, $Arguments)
}

if (-not (Test-Path -LiteralPath $pending)) { exit 10 }
New-Item -ItemType Directory -Force -Path $work | Out-Null
if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force }
Copy-Item -LiteralPath $pending -Destination $staged -Force
Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue
Write-UpdateLog ('Update requested; verifying ' + $staged)

# 1. Authenticode with online revocation checking (WinVerifyTrust, WTD_REVOKE_WHOLECHAIN, excluding the root).
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WsrTrust {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo { public uint cbStruct; [MarshalAs(UnmanagedType.LPWStr)] public string path; public IntPtr hFile; public IntPtr known; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Data { public uint cbStruct; public IntPtr policy; public IntPtr sip; public uint ui; public uint revocation; public uint choice; public IntPtr file; public uint state; public IntPtr hState; public IntPtr url; public uint provFlags; public uint uiContext; public IntPtr signature; }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref Data data);
    public static int Verify(string path) {
        var info = new FileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(FileInfo)), path = path };
        IntPtr pInfo = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
        try {
            Marshal.StructureToPtr(info, pInfo, false);
            var data = new Data { cbStruct = (uint)Marshal.SizeOf(typeof(Data)), ui = 2, revocation = 1, choice = 1, file = pInfo, state = 1, provFlags = 0x80 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.state = 2;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        } finally {
            Marshal.DestroyStructure(pInfo, typeof(FileInfo));
            Marshal.FreeHGlobal(pInfo);
        }
    }
}
'@
$trust = [WsrTrust]::Verify($staged)
if ($trust -ne 0) {
    $hex = '0x{0:X8}' -f $trust
    $reason = 'the Authenticode signature is not valid or not trusted (' + $hex + ')'
    if ($hex -eq '0x80092013') { $reason = 'the revocation servers (CRL/OCSP) of the signing certificate are unreachable (0x80092013); retry when the server can reach them' }
    if ($hex -eq '0x800B010C') { $reason = 'the signing certificate has been REVOKED (0x800B010C)' }
    if ($hex -eq '0x800B0100') { $reason = 'the package is not signed (0x800B0100)' }
    Stop-Update 11 $reason
}

# 2. Publisher pin: the signer's Organization RDN must equal the pinned organisation exactly.
$signer = (Get-AuthenticodeSignature -LiteralPath $staged).SignerCertificate
if ($null -eq $signer) { Stop-Update 11 'no signer certificate found' }
$orgs = @($signer.SubjectName.Decode([System.Security.Cryptography.X509Certificates.X500DistinguishedNameFlags]::UseNewLines) -split "`r?`n" |
    Where-Object { $_ -like 'O=*' } | ForEach-Object { $_.Substring(2).Trim('"', ' ') })
if (-not ($orgs | Where-Object { $_ -eq $Organization })) { Stop-Update 12 ('the signer is not ' + $Organization + ' (' + $signer.Subject + ')') }
if ($SignerThumbprints -notcontains $signer.Thumbprint.ToUpperInvariant()) { Stop-Update 16 ('the signing certificate ' + $signer.Thumbprint + ' is not a pinned release certificate') }

# 3 + 4. Product identity and version, read with the Windows Installer automation interface.
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = Invoke-Com $installer 'OpenDatabase' 'InvokeMethod' @($staged, 0)
    $values = @{}
    foreach ($name in @('UpgradeCode', 'ProductName', 'ProductVersion')) {
        $view = Invoke-Com $db 'OpenView' 'InvokeMethod' @("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='" + $name + "'")
        Invoke-Com $view 'Execute' 'InvokeMethod' $null | Out-Null
        $record = Invoke-Com $view 'Fetch' 'InvokeMethod' $null
        if ($null -ne $record) { $values[$name] = Invoke-Com $record 'StringData' 'GetProperty' @(1) }
        Invoke-Com $view 'Close' 'InvokeMethod' $null | Out-Null
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    }
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($db)
    $db = $null

    $installed = $null
    # PowerShell unrolls the returned StringList into product-code strings (none = empty array).
    $codes = @(Invoke-Com $installer 'RelatedProducts' 'GetProperty' @($UpgradeCode))
    foreach ($code in $codes) {
        if (-not $code) { continue }
        $v = ConvertTo-MsiVersion (Invoke-Com $installer 'ProductInfo' 'GetProperty' @([string]$code, 'VersionString'))
        if ($null -ne $v -and ($null -eq $installed -or $v -gt $installed)) { $installed = $v }
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
} catch {
    Stop-Update 15 ('the package could not be read: ' + $_.Exception.Message)
}

if ($values['UpgradeCode'] -ne $UpgradeCode) { Stop-Update 13 ('UpgradeCode ' + $values['UpgradeCode'] + ' is not WinSmtpRelay') }
$name = [string]$values['ProductName']
if (-not ($name -ceq $ProductName -or $name.StartsWith($ProductName + ' ', [System.StringComparison]::Ordinal))) { Stop-Update 13 ('ProductName ' + $name + ' is not WinSmtpRelay') }
$candidate = ConvertTo-MsiVersion ([string]$values['ProductVersion'])
if ($null -eq $candidate) { Stop-Update 14 ('ProductVersion ' + $values['ProductVersion'] + ' is not valid') }
if ($null -ne $installed -and $candidate -le $installed) { Stop-Update 14 ('version ' + $candidate + ' is not newer than the installed ' + $installed) }

Write-UpdateLog ('Verified ' + $name + ' ' + $candidate + ' (installed ' + $installed + ', signer ' + $signer.Subject + '); installing.')
$p = Start-Process -FilePath msiexec.exe -ArgumentList @('/i', ('"' + $staged + '"'), '/qn', '/norestart', '/l*v', ('"' + $msiLog + '"')) -Wait -PassThru
Start-Sleep -Seconds 2
Start-Service -Name 'WinSmtpRelay' -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
if ($p.ExitCode -eq 0 -or $p.ExitCode -eq 3010) {
    Write-UpdateLog ('Installed ' + $name + ' ' + $candidate + ' (msiexec exit ' + $p.ExitCode + ').')
} else {
    Write-UpdateLog ('msiexec failed with exit code ' + $p.ExitCode + '; see ' + $msiLog) 9102 'Error'
}
exit $p.ExitCode

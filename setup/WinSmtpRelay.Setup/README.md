# WinSmtpRelay MSI Installer

WiX v5 project that produces two MSI variants for WinSmtpRelay.

| Variant | Output | Prerequisite |
|---------|--------|-------------|
| Self-contained | `WinSmtpRelay-{version}-x64-self-contained.msi` | None |
| Framework-dependent | `WinSmtpRelay-{version}-x64.msi` | .NET 10 Runtime (x64) |

Both share the same `UpgradeCode` — installing one replaces the other.

## Versioning and releases

The version is defined in one place: `Directory.Build.props` at the repo root.

```xml
<Version>1.0.0</Version>                     <!-- semantic product version -->
<BuildNumber>80</BuildNumber>                <!-- grows with EVERY release, never reused -->
<VersionSuffix>beta1-build80</VersionSuffix> <!-- pre-release label, ends in -build<BuildNumber> -->
```

| Property | Example | Where it appears |
|----------|---------|-----------------|
| `MsiProductVersion` | `1.0.80` | MSI ProductVersion (major.minor.BuildNumber), `FileVersion` 1.0.80.0 |
| `InformationalVersion` | `1.0.0-beta1-build80` | Admin UI `/api/server/info`, Windows file details |
| MSI product name | `WinSmtpRelay 1.0.0-beta1-build80` | Add/Remove Programs |

Windows Installer compares only the first three version fields, so the build number is the third one: every release is
a strictly greater MSI version and a normal MajorUpgrade replaces the previous build. Installing an older build is
refused (`DowngradeErrorMessage`); there is no same-version upgrade.

**Release:** bump `BuildNumber` / `VersionSuffix`, promote the CHANGELOG `[Unreleased]` section to
`## [1.0.0-beta1-build80] - <date>`, commit, tag `v1.0.0-beta1-build80`, push the tag. `.github/workflows/release.yml`
builds, tests, signs every first-party binary and both MSIs, verifies the signatures and publishes the GitHub Release.
A tag without `-build<N>`, a missing CHANGELOG section or missing signing secrets fail the release.

## Folder layout

| Folder | Content | NetworkService |
|--------|---------|----------------|
| `C:\Program Files\WinSmtpRelay\` | binaries only | Read & Execute |
| `C:\ProgramData\Ardimedia\WinSmtpRelay\` | `appsettings.json`, `appsettings.Production.json` (admin port/bind, written by the installer), optional `appsettings.Local.json`, `winsmtprelay.db` (+ `-wal`, `-shm`), `keys\` (Data Protection), `admin-cert.pfx`, `.migrated` | Modify |
| `...\WinSmtpRelay\updates\` | update drop folder (the service downloads here) | Modify |
| `...\WinSmtpRelay\updater-work\` | SYSTEM-only copy the updater verifies and installs, `update.log`, `msiexec.log` | none |

Every folder gets an explicit ACL with inheritance broken (SYSTEM and Administrators full control); a failure fails the
installation. The service uses the data folder as its content root, so the configuration loads by the standard
`appsettings.json` / `appsettings.{Environment}.json` rules, plus `appsettings.Local.json` for machine-local secrets.

## Upgrading from a version that kept its data in the program folder

Before `InstallFiles`, the `MigrateLegacyData` custom action (as SYSTEM) moves the database with its side files, the
key ring, the admin certificate and the appsettings files from the program folder into the data folder — exactly once
(`.migrated` marker), never overwriting a file that already exists there (the legacy copy then stays and is logged),
every step in the MSI log. If the installation fails, the rollback moves everything back and restores the program
folder's previous ACL. As a fallback, the service performs the same move on its first start and refuses to start if
it cannot (so it never starts on an empty database).

## Installer properties

| Property | Default | Effect |
|----------|---------|--------|
| `NETWORKACCESS=1` | off | Admin UI binds to the network and gets a firewall rule (Domain + Private) |
| `ADMINUIFIREWALLSCOPE=any` | local subnet | Admin-UI rule open to every remote address |
| `ADMINUIPORT` | 8025 (or the next free) | Admin UI port; must be 1–65535, otherwise the installation stops |
| `FIREWALLSMTP` | `1` | Opens port 25 (Domain + Private) |
| `FIREWALLSUBMISSION=1` | off | Opens port 587 |
| `FIREWALLSMTPS=1` | off | Opens port 465 |
| `FIREWALLPUBLIC=1` | off | Also opens the chosen ports on the Public profile |
| `RESETADMINPASSWORD=1` | off | Removes all administrators on the next start (first-run setup again) |
| `REMOVEDATA=1` | off | On uninstall, deletes `C:\ProgramData\Ardimedia\WinSmtpRelay` (database, keys, configuration) |

Pass `0` to switch a firewall choice off. The choices are recorded under
`HKLM\SOFTWARE\ARDIMEDIA\WinSmtpRelay.Setup` (writable only by administrators) and restored on the next upgrade;
a value on the command line always wins. The first upgrade from a version without recorded choices keeps 587, 465 and
the Public profile open as before — pass `FIREWALLSUBMISSION=0 FIREWALLSMTPS=0 FIREWALLPUBLIC=0` to close them.

## Self-update

The service downloads the MSI from the official release URL, checks it, stages it in `updates\pending.msi` and writes
Application event 9100. The SYSTEM scheduled task `WinSmtpRelayUpdate` (registered by the `RegisterUpdateTask` custom
action from the task XML and `updater\Update-WinSmtpRelay.ps1`, both embedded in the custom-action assembly — never
read from a folder the service can write) copies it into `updater-work\` and re-checks: Authenticode signature with
online revocation, signer `O=ARDIMEDIA` AND a pinned certificate thumbprint, UpgradeCode and ProductName of
WinSmtpRelay, and a version strictly greater than the installed one. Only then it runs msiexec.

## Building locally

```bash
dotnet publish src/WinSmtpRelay.Service/WinSmtpRelay.Service.csproj -c Release -r win-x64 --self-contained false -o publish-fd/
dotnet build setup/WinSmtpRelay.Setup/WinSmtpRelay.Setup.wixproj -c Release -p:HarvestPath=..\..\publish-fd\ -p:SelfContained=false
```

Output: `setup/WinSmtpRelay.Setup/bin/x64/Release/WinSmtpRelay.Setup.msi` (version from `Directory.Build.props`).

## Important: UpgradeCode

The `UpgradeCode` GUID (`2cbb38d8-5035-40db-b30b-0e55f24ec496`) in `Package.wxs` must **never change**. It is the
permanent identity that allows MSI upgrades to detect and replace previous installations, and the self-update checks it.

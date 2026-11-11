using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.DataLocation;

namespace WinSmtpRelay.Service.Update;

/// <summary>
/// Unattended self-update for the low-privilege relay service. It downloads the MSI from the pinned official
/// release URL, verifies its Authenticode signature (with online revocation), the publisher, the product identity
/// and that it is newer than the installed version, stages it as <c>pending.msi</c> in the drop folder
/// (<c>%ProgramData%\Ardimedia\WinSmtpRelay\updates</c>), then fires the elevated SYSTEM updater scheduled task
/// (registered by the installer) via its Event-Log trigger. The actual <c>msiexec</c> install runs as SYSTEM
/// out-of-process, because this account (NetworkService) cannot install an MSI; that task re-verifies everything
/// before installing.
/// </summary>
public class UpdateService(
    IHttpClientFactory httpClientFactory,
    IOptions<UpdateOptions> options,
    ILogger<UpdateService> logger) : IUpdateService
{
    public bool IsSupported => OperatingSystem.IsWindows() && options.Value.Enabled;

    public async Task<UpdateLaunchResult> StartUpdateAsync(string assetUrl, string targetVersion, CancellationToken ct = default)
    {
        var opts = options.Value;

        if (!OperatingSystem.IsWindows())
            return new(false, "Self-update is only supported on Windows.");
        if (!opts.Enabled)
            return new(false, "Self-update is disabled (Update:Enabled = false).");

        // Pin downloads to the official release host (no SSRF / arbitrary fetch).
        if (string.IsNullOrWhiteSpace(assetUrl) ||
            !assetUrl.StartsWith(opts.AllowedAssetUrlPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new(false, "Refusing to download: the URL is not an official HTTPS release asset.");

        var dropFolder = ResolveDropFolder(opts);
        try
        {
            Directory.CreateDirectory(dropFolder);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "creating the update drop folder failed");
            return new(false, $"Could not prepare the update folder: {ex.Message}");
        }

        var tempPath = Path.Combine(dropFolder, "download.tmp");
        var pendingPath = Path.Combine(dropFolder, "pending.msi");

        // 1) Download the package.
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WinSmtpRelay-Updater");

            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tempPath);
            await resp.Content.CopyToAsync(fs, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "downloading the update failed");
            TryDelete(tempPath);
            return new(false, $"Download failed: {ex.Message}");
        }

        // 2) Verify (pre-check — the SYSTEM updater re-verifies all of it independently before installing):
        //    Authenticode signature incl. online revocation, the pinned publisher (O=), and that the package is
        //    WinSmtpRelay's MSI (UpgradeCode + ProductName) with a version strictly greater than the installed one.
        //    The organization and product pins are compiled in (UpdatePackageIdentity); Update:SignerThumbprints can only
        //    change which certificates this pre-check accepts — the SYSTEM updater keeps its own compiled-in list.
        if (!VerifyPackage(tempPath, out var verifyError))
        {
            logger.LogWarning("Update package {Version} rejected: {Error}", targetVersion, verifyError);
            TryDelete(tempPath);
            return new(false, verifyError ?? "The installer did not pass verification.");
        }

        // 3) Stage as pending.msi, then fire the elevated updater task.
        try
        {
            if (File.Exists(pendingPath)) File.Delete(pendingPath);
            File.Move(tempPath, pendingPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "staging the pending update failed");
            TryDelete(tempPath);
            return new(false, $"Could not stage the update: {ex.Message}");
        }

        if (!TriggerUpdaterTask(opts, targetVersion, out var triggerError))
        {
            logger.LogError("could not trigger the updater task: {Error}", triggerError);
            return new(false, $"The update was downloaded and verified, but the elevated updater task could not be triggered: {triggerError}. Is the installer's update task registered (re-run/repair the MSI)?");
        }

        logger.LogWarning("Self-update {Version} staged and triggered — the service will stop, install, and restart.", targetVersion);
        return new(true, $"Update {targetVersion} verified and handed to the system updater. The service will stop, install, and restart shortly.");
    }

    // The SYSTEM updater task watches %ProgramData%\Ardimedia\WinSmtpRelay\updates (fixed in the task). A configured
    // DropFolder is still honoured, but the task does not pick a package up from anywhere else.
    private string ResolveDropFolder(UpdateOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.DropFolder))
            return RelayDataDirectory.UpdateDropFolder;
        logger.LogWarning("Update:DropFolder is set to {Folder}; the SYSTEM updater only installs packages from {Default}.",
            opts.DropFolder, RelayDataDirectory.UpdateDropFolder);
        return opts.DropFolder;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool VerifyPackage(string path, out string? error)
    {
        if (!AuthenticodeVerifier.Verify(path, UpdatePackageIdentity.SignerOrganization, PinnedThumbprints(), out var signer, out error))
            return false;
        logger.LogInformation("Update package signature valid — signed by {Signer}", signer);

        MsiPackageInspector.PackageInfo package;
        try
        {
            package = MsiPackageInspector.ReadPackage(path);
        }
        catch (Exception ex)
        {
            error = $"The downloaded file could not be read as an installer package: {ex.Message}";
            return false;
        }

        if (!UpdatePackageIdentity.IsOurProduct(package.UpgradeCode, package.ProductName, out var reason))
        {
            error = $"The installer is signed, but {reason}. The update was NOT installed.";
            return false;
        }

        // The installed MSI version; the running binary's file version (= MSI version + ".0") as the fallback.
        var installed = MsiPackageInspector.InstalledVersion(UpdatePackageIdentity.UpgradeCode)
            ?? typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!UpdatePackageIdentity.IsNewer(package.ProductVersion, installed, out reason))
        {
            error = $"The installer is WinSmtpRelay, but {reason}. The self-update installs only newer builds (no downgrade, no reinstall).";
            return false;
        }

        logger.LogInformation("Update package is {Product} {Version} (installed: {Installed})",
            package.ProductName, package.ProductVersion, installed ?? "unknown");
        return true;
    }

    // The certificate thumbprints this pre-check accepts: Update:SignerThumbprints (a list, for a certificate rollover),
    // defaulting to the compiled-in release certificate. This only widens or narrows the service-side PRE-check — the
    // SYSTEM updater script carries its own compiled-in list and never reads the (service-writable) configuration.
    private IReadOnlyList<string> PinnedThumbprints()
    {
        var configured = options.Value.SignerThumbprints;
        return configured is { Length: > 0 } ? configured : UpdatePackageIdentity.DefaultSignerThumbprints;
    }

    // Fires the SYSTEM updater scheduled task by writing its trigger event to the Windows Event Log. The
    // task (registered by the installer, immutable to this low-privilege account) listens for this source +
    // event ID, then re-verifies the staged package and installs it as SYSTEM.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool TriggerUpdaterTask(UpdateOptions opts, string targetVersion, out string? error)
    {
        error = null;
        try
        {
            EventLog.WriteEntry(opts.TriggerEventSource,
                $"Self-update requested: installing {targetVersion} from the staged package.",
                EventLogEntryType.Information, opts.TriggerEventId);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Service.Update;

/// <summary>
/// Unattended self-update for the low-privilege relay service. It downloads the MSI from the pinned official
/// release URL, verifies its Authenticode signature + publisher, stages it as <c>pending.msi</c> in a drop
/// folder, then fires the elevated SYSTEM updater scheduled task (registered by the installer) via its
/// Event-Log trigger. The actual <c>msiexec</c> install runs as SYSTEM out-of-process, because this account
/// (NetworkService) cannot install an MSI; that task re-verifies the signature before installing.
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

        // 2) Verify Authenticode + publisher (pre-check — the SYSTEM updater re-verifies before installing).
        string? signer = null, verifyError = null;
        if (!OperatingSystem.IsWindows() ||
            !AuthenticodeVerifier.Verify(tempPath, opts.ExpectedPublisher, out signer, out verifyError))
        {
            logger.LogWarning("Update package failed signature verification: {Error}", verifyError);
            TryDelete(tempPath);
            return new(false, verifyError ?? "The installer is not validly signed.");
        }
        logger.LogInformation("Update package verified — signed by {Signer}", signer);

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

    private static string ResolveDropFolder(UpdateOptions opts) =>
        string.IsNullOrWhiteSpace(opts.DropFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinSmtpRelay", "updates")
            : opts.DropFolder;

    // Fires the SYSTEM updater scheduled task by writing its trigger event to the Windows Event Log. The
    // task (registered by the installer, immutable to this low-privilege account) listens for this source +
    // event ID, then re-verifies the staged package's signature and installs it as SYSTEM.
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

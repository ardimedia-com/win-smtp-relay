namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Drives the unattended software self-update: validates the asset URL, downloads the MSI, verifies its
/// Authenticode signature and publisher, drops it for the elevated SYSTEM updater task, and triggers that
/// task. The interface lives in Core so the Blazor admin UI can invoke it without referencing the host
/// implementation. The actual privileged install runs out-of-process as SYSTEM (the service account is
/// least-privilege and cannot install an MSI itself).
/// </summary>
public interface IUpdateService
{
    /// <summary>True if remote self-update is enabled and supported on this host (Windows).</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Downloads and verifies the MSI at <paramref name="assetUrl"/>, then hands it to the elevated updater
    /// task. Returns whether the install was successfully scheduled. The service is expected to restart
    /// shortly after; callers poll <c>/api/server/info</c> to detect the new version.
    /// </summary>
    Task<UpdateLaunchResult> StartUpdateAsync(string assetUrl, string targetVersion, CancellationToken ct = default);
}

/// <summary>Outcome of scheduling an unattended update.</summary>
/// <param name="Launched">True if the verified MSI was handed to the elevated updater task.</param>
/// <param name="Message">Human-readable status / reason (shown in the admin UI).</param>
public sealed record UpdateLaunchResult(bool Launched, string Message);

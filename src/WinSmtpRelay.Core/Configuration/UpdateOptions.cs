namespace WinSmtpRelay.Core.Configuration;

/// <summary>
/// Settings for the unattended software self-update (Update page → "Install on this server"). The relay
/// service runs as the low-privilege NetworkService account and cannot install an MSI itself; it downloads
/// and verifies the package, then hands it to an elevated SYSTEM scheduled task (registered by the
/// installer) that re-verifies the Authenticode signature before installing. These options bound from the
/// <c>Update</c> appsettings section.
/// </summary>
public class UpdateOptions
{
    public const string SectionName = "Update";

    /// <summary>Master switch for the remote-triggered self-update. Off ⇒ the Install button is unavailable.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// An update asset URL must start with this prefix to be downloaded — pins downloads to this project's
    /// official GitHub releases so the relay can't be told to fetch an arbitrary file (SSRF / supply-chain).
    /// </summary>
    public string AllowedAssetUrlPrefix { get; set; } = "https://github.com/ardimedia-com/win-smtp-relay/releases/download/";

    /// <summary>
    /// The downloaded MSI's signer Organization (the certificate subject's <c>O=</c> RDN) must EQUAL this
    /// exactly (case-insensitive) — publisher pinning on the O field, not a loose subject substring. The
    /// SYSTEM updater task applies the same pin independently. Keep in sync with the value baked into
    /// <c>update-task.xml</c> if changed.
    /// </summary>
    public string ExpectedPublisher { get; set; } = "ARDIMEDIA";

    /// <summary>Windows service name the installer restarts after the upgrade.</summary>
    public string ServiceName { get; set; } = "WinSmtpRelay";

    /// <summary>
    /// Folder the verified MSI is dropped into for the SYSTEM updater task to pick up. Empty ⇒
    /// <c>%ProgramData%\WinSmtpRelay\updates</c>. Must be writable by the service account and readable by SYSTEM.
    /// </summary>
    public string DropFolder { get; set; } = "";

    /// <summary>Event-Log source the service writes the trigger event to (must match the registered source).</summary>
    public string TriggerEventSource { get; set; } = "WinSmtpRelay.Service";

    /// <summary>Event ID the SYSTEM updater task's event trigger listens for. Writing it fires the task.</summary>
    public int TriggerEventId { get; set; } = 9100;
}

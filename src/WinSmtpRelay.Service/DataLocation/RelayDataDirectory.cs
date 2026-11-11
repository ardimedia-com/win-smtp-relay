using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace WinSmtpRelay.DataLocation;

/// <summary>
/// Where the relay keeps its writable data (decision T1076).
/// <para>
/// An installed service keeps <b>everything it writes</b> in <c>%ProgramData%\Ardimedia\WinSmtpRelay\</c>: the
/// SQLite database (+ <c>-wal</c>/<c>-shm</c>), the Data Protection key ring (<c>keys\</c>), the self-signed admin
/// certificate (<c>admin-cert.pfx</c>), the configuration (<c>appsettings.json</c>,
/// <c>appsettings.Production.json</c> written by the installer, the optional <c>appsettings.Local.json</c> for
/// machine-local secrets) and the update drop folder (<c>updates\</c>). The program folder holds only binaries and is
/// read-only for the service account. That folder is the host's <b>content root</b>, so ASP.NET Core loads the
/// configuration from it by its standard rules — no custom loader.
/// </para>
/// <para>
/// When the program is NOT running as a Windows service (a developer's <c>dotnet run</c>, a test host) the data
/// stays next to the executable as before, so a development machine never touches an installed relay's data.
/// </para>
/// </summary>
public static class RelayDataDirectory
{
    /// <summary>File name of the optional machine-local secrets file (gitignored in the repository).</summary>
    public const string LocalSettingsFileName = "appsettings.Local.json";

    /// <summary><c>%ProgramData%\Ardimedia\WinSmtpRelay</c> — the data directory of an installed service.</summary>
    public static string ProgramDataPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ardimedia", "WinSmtpRelay");

    /// <summary>
    /// The update drop folder the SYSTEM updater task watches. Always under ProgramData (the task's path is fixed),
    /// also in a development run.
    /// </summary>
    public static string UpdateDropFolder => Path.Combine(ProgramDataPath, "updates");

    /// <summary>The data directory for this process.</summary>
    public static string Resolve(bool runningAsService) =>
        runningAsService ? ProgramDataPath : AppContext.BaseDirectory;

    /// <summary>
    /// Adds <c>appsettings.Local.json</c> from the content root right after <c>appsettings.{Environment}.json</c>, so it
    /// overrides both JSON files but environment variables and the command line still win — the precedence the
    /// framework gives every other JSON file.
    /// </summary>
    public static void AddLocalSettings(IConfigurationBuilder configuration, string contentRoot, string environmentName)
    {
        var local = new JsonConfigurationSource
        {
            Path = Path.Combine(contentRoot, LocalSettingsFileName),
            Optional = true,
            ReloadOnChange = true,
        };
        local.ResolveFileProvider();

        var sources = configuration.Sources;
        var environmentFile = $"appsettings.{environmentName}.json";
        int index = -1;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource json &&
                (string.Equals(json.Path, environmentFile, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(json.Path, "appsettings.json", StringComparison.OrdinalIgnoreCase)))
                index = i;
        }

        if (index < 0)
            sources.Add(local);
        else
            sources.Insert(index + 1, local);
    }
}

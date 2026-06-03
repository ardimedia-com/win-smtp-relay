namespace WinSmtpRelay.Core.Configuration;

public class AdminUiOptions
{
    public const string SectionName = "AdminUi";

    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 8025;

    /// <summary>
    /// Bind address for the admin UI/API. Defaults to loopback so the management plane is
    /// local-only unless explicitly exposed (use a reverse proxy or change deliberately).
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Serve the admin plane over HTTPS. Requires a certificate (configured below) or, in Development, the ASP.NET Core dev cert.</summary>
    public bool UseHttps { get; set; } = true;

    /// <summary>Optional PFX path for the admin HTTPS certificate. If unset, the dev cert is used in Development.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Password for <see cref="CertificatePath"/>, if any.</summary>
    public string? CertificatePassword { get; set; }
}

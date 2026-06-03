namespace WinSmtpRelay.Core.Configuration;

public class SmtpListenerOptions
{
    public const string SectionName = "SmtpListener";

    // NOTE: bound collections must NOT be pre-populated. IConfiguration.Bind APPENDS to an
    // existing List rather than replacing it, so a default item here plus one from
    // appsettings produced duplicates (two :25 endpoints, doubled AllowedNetworks).
    // appsettings.json supplies these; ConfigurationSeeder copies them into the DB.
    public List<EndpointOptions> Endpoints { get; set; } = [];

    public int MaxMessageSizeBytes { get; set; } = 25 * 1024 * 1024; // 25 MB
    public int MaxConnections { get; set; } = 100;
    public List<string> AllowedNetworks { get; set; } = [];
    public List<string> AcceptedDomains { get; set; } = [];
    public string? PickupFolder { get; set; }
    public int PickupFolderPollIntervalSeconds { get; set; } = 5;
}

public class EndpointOptions
{
    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 25;
    public bool RequireTls { get; set; }
    public bool ImplicitTls { get; set; }
    public bool RequireAuth { get; set; }
}

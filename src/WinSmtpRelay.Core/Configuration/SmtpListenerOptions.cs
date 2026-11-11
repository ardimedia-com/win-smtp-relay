namespace WinSmtpRelay.Core.Configuration;

public class SmtpListenerOptions
{
    public const string SectionName = "SmtpListener";

    // NOTE: bound collections must NOT be pre-populated. IConfiguration.Bind APPENDS to an
    // existing List rather than replacing it, so a default item here plus one from
    // appsettings produced duplicates (two :25 endpoints, doubled AllowedNetworks).
    // appsettings.json supplies these; ConfigurationSeeder copies them into the DB.
    public List<EndpointOptions> Endpoints { get; set; } = [];

    /// <summary>
    /// Largest message accepted, in bytes. Enforced on the SIZE= parameter at MAIL AND on the bytes actually
    /// received in DATA: a client that sends more is answered 552 and disconnected.
    /// </summary>
    public int MaxMessageSizeBytes { get; set; } = 25 * 1024 * 1024; // 25 MB

    /// <summary>Concurrent SMTP sessions across all endpoints; a connection beyond it is answered 421 and closed. 0 = unlimited.</summary>
    public int MaxConnections { get; set; } = 100;

    /// <summary>Concurrent SMTP sessions from one client IP; a connection beyond it is answered 421 and closed. 0 = unlimited.</summary>
    public int MaxConnectionsPerIp { get; set; } = 20;

    /// <summary>Recipients accepted per message; each further RCPT TO is answered 452 4.5.3 (RFC 5321 §4.5.3.1.10). 0 = unlimited.</summary>
    public int MaxRecipientsPerMessage { get; set; } = 100;

    /// <summary>
    /// A message that already carries more <c>Received:</c> headers than this is refused (554 5.4.6) — it
    /// is looping between relays. RFC 5321 §6.3 suggests at least 100 as the hard limit for MTAs that only
    /// count; 30 is the conventional practical limit for a relay that sits at the edge.
    /// </summary>
    public int MaxReceivedHeaders { get; set; } = 30;
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

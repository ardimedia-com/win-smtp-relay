namespace WinSmtpRelay.Core.Configuration;

public class DeliveryOptions
{
    public const string SectionName = "Delivery";

    public int MaxConcurrentDeliveries { get; set; } = 4;
    public int MaxRetryHours { get; set; } = 48;
    public int[] RetryIntervalsMinutes { get; set; } = [1, 5, 30, 120, 480, 1440];
    public string? SmartHost { get; set; }
    public int SmartHostPort { get; set; } = 587;
    public string? SmartHostUsername { get; set; }
    public string? SmartHostPassword { get; set; }
    public bool OpportunisticTls { get; set; } = true;

    /// When true, delivery via the global smart host requires TLS (mandatory STARTTLS) and fails if the
    /// server will not negotiate it. Does not apply to direct-MX delivery, which stays opportunistic.
    public bool RequireTls { get; set; } = false;
    public int ConnectTimeoutSeconds { get; set; } = 30;

    /// Per-domain routing: domain pattern to upstream relay config.
    /// Checked before global SmartHost. Supports wildcard prefix (e.g. "*.example.com").
    public List<DomainRouteOptions> DomainRoutes { get; set; } = [];
}

public class DomainRouteOptions
{
    public string DomainPattern { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

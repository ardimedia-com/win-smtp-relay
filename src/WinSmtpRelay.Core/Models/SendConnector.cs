namespace WinSmtpRelay.Core.Models;

public class SendConnector : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public string Name { get; set; } = "";
    public string? SmartHost { get; set; }
    public int SmartHostPort { get; set; } = 587;
    public string? Username { get; set; }
    public string? EncryptedPassword { get; set; }
    public bool OpportunisticTls { get; set; } = true;
    public bool RequireTls { get; set; }
    public bool IsDefault { get; set; }
    public int MaxConcurrentDeliveries { get; set; } = 4;
    public int MaxRetryHours { get; set; } = 48;
    public string RetryIntervalsMinutes { get; set; } = "1,5,30,120,480,1440";
    public int ConnectTimeoutSeconds { get; set; } = 30;
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Optional EHLO/HELO hostname override for deliveries through this connector. Needed when the
    /// connector sends from a different egress IP than the host default — the announced name should
    /// match that IP's PTR record. Falls back to the host's public hostname (Settings → Sending
    /// identity), then the machine FQDN. See <see cref="EhloHostname"/>.
    /// </summary>
    public string? EhloDomain { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<DomainRoute> DomainRoutes { get; set; } = [];
}

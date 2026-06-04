namespace WinSmtpRelay.Core.Models;

public class ReceiveConnector : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public string Name { get; set; } = "";
    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 25;
    public bool RequireTls { get; set; }
    public bool ImplicitTls { get; set; }
    public bool RequireAuth { get; set; }
    public int MaxMessageSizeBytes { get; set; } = 25 * 1024 * 1024;
    public int MaxConnections { get; set; } = 100;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

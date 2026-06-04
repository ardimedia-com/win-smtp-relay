namespace WinSmtpRelay.Core.Models;

public class DomainRoute : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public required string DomainPattern { get; set; }
    public int SendConnectorId { get; set; }
    public SendConnector? SendConnector { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

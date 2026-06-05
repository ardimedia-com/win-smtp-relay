namespace WinSmtpRelay.Core.Models;

public class SenderRewriteEntry : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public string FromPattern { get; set; } = "";
    public string ToAddress { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

namespace WinSmtpRelay.Core.Models;

public class HeaderRewriteEntry : ITenantOwned
{
    public int Id { get; set; }
    public int TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    public string HeaderName { get; set; } = "";
    public string? MatchValue { get; set; }
    public string Action { get; set; } = "Set";
    public string? NewValue { get; set; }
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

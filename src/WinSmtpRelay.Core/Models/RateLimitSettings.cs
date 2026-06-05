namespace WinSmtpRelay.Core.Models;

public class RateLimitSettings
{
    public int Id { get; set; }
    public int MaxConnectionsPerIpPerMinute { get; set; } = 30;
    public int MaxMessagesPerSenderPerMinute { get; set; } = 20;
    public int MaxMessagesPerSenderPerDay { get; set; } = 1000;
    public int FailedAuthBanThreshold { get; set; } = 5;
    public int FailedAuthBanMinutes { get; set; } = 30;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

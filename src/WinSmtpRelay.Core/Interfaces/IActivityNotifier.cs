namespace WinSmtpRelay.Core.Interfaces;

public interface IActivityNotifier
{
    Task NotifyMessageReceivedAsync(string messageId, string sender, string recipients, int sizeBytes, int tenantId);
    Task NotifyDeliveryAttemptAsync(string messageId, string recipient, string statusCode, string? remoteServer, int tenantId);
    Task NotifyConnectionAsync(string sourceIp, string eventType);
    Task NotifyQueueChangedAsync();
}

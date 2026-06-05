namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// No-op implementation used when SignalR is not available (Admin UI disabled).
/// </summary>
public class NullActivityNotifier : IActivityNotifier
{
    public Task NotifyMessageReceivedAsync(string messageId, string sender, string recipients, int sizeBytes, int tenantId) => Task.CompletedTask;
    public Task NotifyDeliveryAttemptAsync(string messageId, string recipient, string statusCode, string? remoteServer, int tenantId) => Task.CompletedTask;
    public Task NotifyConnectionAsync(string sourceIp, string eventType) => Task.CompletedTask;
    public Task NotifyQueueChangedAsync() => Task.CompletedTask;
}

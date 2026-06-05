using Microsoft.AspNetCore.SignalR;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.AdminApi;

public class ActivityHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }
}

public class ActivityNotifier(IHubContext<ActivityHub> hub) : IActivityNotifier
{
    public async Task NotifyMessageReceivedAsync(string messageId, string sender, string recipients, int sizeBytes)
    {
        await hub.Clients.All.SendAsync("MessageReceived", new
        {
            MessageId = messageId,
            Sender = sender,
            Recipients = recipients,
            SizeBytes = sizeBytes,
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }

    public async Task NotifyDeliveryAttemptAsync(string messageId, string recipient, string statusCode, string? remoteServer)
    {
        await hub.Clients.All.SendAsync("DeliveryAttempt", new
        {
            MessageId = messageId,
            Recipient = recipient,
            StatusCode = statusCode,
            RemoteServer = remoteServer,
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }

    public async Task NotifyConnectionAsync(string sourceIp, string eventType)
    {
        await hub.Clients.All.SendAsync("SmtpConnection", new
        {
            SourceIp = sourceIp,
            EventType = eventType,
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }

    public async Task NotifyQueueChangedAsync()
    {
        await hub.Clients.All.SendAsync("QueueChanged");
    }
}

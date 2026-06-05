using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.AdminApi;

public class ActivityHub : Hub
{
    // The group every host-admin (all-tenants scope) connection joins, and which message/delivery
    // notifications are always also sent to, so host admins see all tenants' activity.
    public const string AllTenantsGroup = "all";

    public override async Task OnConnectedAsync()
    {
        // Mail metadata is tenant-owned, so a connection only joins the group for the tenant it may
        // see — mirroring TenantContextResolver. Host admins viewing all tenants join "all".
        await Groups.AddToGroupAsync(Context.ConnectionId, ResolveGroup(Context.User));
        await base.OnConnectedAsync();
    }

    /// <summary>The SignalR group name for a connection, derived from its tenant claims.</summary>
    public static string ResolveGroup(ClaimsPrincipal? user)
    {
        if (user?.HasClaim(RelayClaimTypes.IsHostAdmin, "true") == true)
        {
            return int.TryParse(user.FindFirst(RelayClaimTypes.ActiveTenant)?.Value, out var active)
                ? TenantGroup(active)
                : AllTenantsGroup; // host admin, all-tenants scope
        }
        return int.TryParse(user?.FindFirst(RelayClaimTypes.TenantId)?.Value, out var tenantId)
            ? TenantGroup(tenantId)
            : TenantGroup(-1); // authenticated but no tenant → a group that receives nothing
    }

    public static string TenantGroup(int tenantId) => $"tenant:{tenantId}";
}

public class ActivityNotifier(IHubContext<ActivityHub> hub) : IActivityNotifier
{
    public async Task NotifyMessageReceivedAsync(string messageId, string sender, string recipients, int sizeBytes, int tenantId)
    {
        await hub.Clients.Groups([ActivityHub.AllTenantsGroup, ActivityHub.TenantGroup(tenantId)]).SendAsync("MessageReceived", new
        {
            MessageId = messageId,
            Sender = sender,
            Recipients = recipients,
            SizeBytes = sizeBytes,
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }

    public async Task NotifyDeliveryAttemptAsync(string messageId, string recipient, string statusCode, string? remoteServer, int tenantId)
    {
        await hub.Clients.Groups([ActivityHub.AllTenantsGroup, ActivityHub.TenantGroup(tenantId)]).SendAsync("DeliveryAttempt", new
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

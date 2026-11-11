using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Storage;

/// <summary>
/// EF-backed <see cref="IMessageBodyAccess"/>: consent is checked against the stored memberships / API key (not
/// the claims in a possibly hours-old cookie), and every successful read is audited before the body is returned.
/// </summary>
public class MessageBodyAccess(
    RelayDbContext db,
    ICurrentActor actor,
    IAdminAuditService audit,
    ILogger<MessageBodyAccess> logger) : IMessageBodyAccess
{
    public async Task<MessageBodyReadResult> ReadAsync(long messageId, string purpose, CancellationToken ct = default)
    {
        // The ambient tenant filter applies: a tenant-scoped caller never learns that another tenant's message exists.
        var message = await db.QueuedMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.TenantId })
            .FirstOrDefaultAsync(ct);
        if (message is null)
            return new MessageBodyReadResult(MessageBodyReadStatus.NotFound);

        if (!await HasConsentAsync(message.TenantId, ct))
        {
            logger.LogWarning(
                "Message body {MessageId} of tenant {TenantId} refused for {Actor}: no tenant-admin membership in that tenant",
                messageId, message.TenantId, actor.Email ?? "(system)");
            return new MessageBodyReadResult(MessageBodyReadStatus.Forbidden, TenantId: message.TenantId);
        }

        var body = await db.QueuedMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => m.RawMessage)
            .FirstOrDefaultAsync(ct);
        if (body is null or { Length: 0 })
            return new MessageBodyReadResult(MessageBodyReadStatus.NoBody, TenantId: message.TenantId);

        // Audited BEFORE the body leaves the service: a read that is not recorded must not happen.
        await audit.WriteAsync(AdminAuditActions.MessageBodyRead, actor, tenantId: message.TenantId,
            detail: $"message {messageId} ({purpose})", ct: ct);
        return new MessageBodyReadResult(MessageBodyReadStatus.Ok, body, message.TenantId);
    }

    private async Task<bool> HasConsentAsync(int tenantId, CancellationToken ct)
    {
        if (actor.UserId is int userId)
        {
            // A tenant-admin membership in exactly this tenant — granted, or self-granted as break-glass.
            return await db.AdminMemberships.AsNoTracking().AnyAsync(
                m => m.UserId == userId && m.TenantId == tenantId && m.Role == RelayRoles.TenantAdmin, ct);
        }

        if (actor.ApiKeyId is int keyId)
        {
            // A host-level key has no membership in any tenant, so it can never read bodies — narrowing it with
            // X-Relay-Tenant only selects data, it does not grant the consent a membership represents.
            return await db.ApiKeys.AsNoTracking().AnyAsync(
                k => k.Id == keyId && k.IsEnabled && k.TenantId == tenantId && k.Role == RelayRoles.TenantAdmin, ct);
        }

        return false;
    }
}

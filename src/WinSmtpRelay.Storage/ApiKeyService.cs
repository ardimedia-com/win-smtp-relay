using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

// Key lifecycle is audited at the SERVICE (like the config services): creating or revoking a
// credential must leave a trace no matter which caller (UI page, API endpoint) performed it.
public class ApiKeyService(
    RelayDbContext db,
    ICurrentActor actor,
    IAdminAuditService audit) : IApiKeyService
{
    private const string Prefix = "wsr_";
    private const int PrefixStoreLength = 12; // "wsr_" + 8 chars, enough to narrow lookups

    public async Task<IReadOnlyList<ApiKey>> GetAllAsync(int? tenantId, CancellationToken cancellationToken)
    {
        var query = db.ApiKeys.AsNoTracking();
        if (tenantId is not null)
            query = query.Where(k => k.TenantId == tenantId);

        return await query.OrderByDescending(k => k.Id).ToListAsync(cancellationToken);
    }

    public async Task<(ApiKey Key, string Plaintext)> CreateAsync(
        int? tenantId, string name, string role, string? scopes, DateTimeOffset? expiresUtc, CancellationToken cancellationToken)
    {
        var plaintext = GenerateKey();
        var entity = new ApiKey
        {
            TenantId = tenantId,
            Name = name,
            Role = role,
            Scopes = ApiKeyScopes.Normalize(ApiKeyScopes.Parse(scopes)),
            KeyPrefix = plaintext[..PrefixStoreLength],
            KeyHash = Hash(plaintext),
            IsEnabled = true,
            CreatedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = expiresUtc
        };

        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(AdminAuditActions.ApiKeyCreated, actor, tenantId: entity.TenantId,
            detail: $"{entity.Name} ({entity.KeyPrefix}…) role={entity.Role} scopes={entity.Scopes ?? "(read-only)"}",
            ct: cancellationToken);
        return (entity, plaintext);
    }

    public async Task UpdateScopesAsync(int id, string? scopes, CancellationToken cancellationToken)
    {
        var existing = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (existing is null)
            return;

        existing.Scopes = ApiKeyScopes.Normalize(ApiKeyScopes.Parse(scopes));
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(AdminAuditActions.ApiKeyUpdated, actor, tenantId: existing.TenantId,
            detail: $"{existing.Name} ({existing.KeyPrefix}…) scopes={existing.Scopes ?? "(read-only)"}",
            ct: cancellationToken);
    }

    public async Task<ApiKey?> ValidateAsync(string presentedKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presentedKey) || presentedKey.Length < PrefixStoreLength || !presentedKey.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        var prefix = presentedKey[..PrefixStoreLength];
        var presentedHash = Hash(presentedKey);
        var presentedHashBytes = Encoding.ASCII.GetBytes(presentedHash);

        // Narrow by indexed prefix, then timing-safe compare the hash.
        var candidates = await db.ApiKeys.Where(k => k.KeyPrefix == prefix).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var candidate in candidates)
        {
            var candidateHashBytes = Encoding.ASCII.GetBytes(candidate.KeyHash);
            if (candidateHashBytes.Length == presentedHashBytes.Length &&
                CryptographicOperations.FixedTimeEquals(candidateHashBytes, presentedHashBytes))
            {
                if (!candidate.IsEnabled || (candidate.ExpiresUtc is not null && candidate.ExpiresUtc <= now))
                    return null;

                // Reject keys belonging to a disabled tenant (host-level keys have no tenant).
                if (candidate.TenantId is int keyTenantId &&
                    !await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == keyTenantId && t.IsEnabled, cancellationToken))
                {
                    return null;
                }

                candidate.LastUsedUtc = now;
                await db.SaveChangesAsync(cancellationToken);
                return candidate;
            }
        }

        return null;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        // Load-then-delete so the audit row can name the key that was revoked (the row itself is
        // hard-deleted; the denormalised detail is what survives).
        var existing = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (existing is null)
            return;

        await db.ApiKeys.Where(k => k.Id == id).ExecuteDeleteAsync(cancellationToken);
        await audit.WriteAsync(AdminAuditActions.ApiKeyDeleted, actor, tenantId: existing.TenantId,
            detail: $"{existing.Name} ({existing.KeyPrefix}…) role={existing.Role}", ct: cancellationToken);
    }

    private static string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return Prefix + token;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes);
    }
}

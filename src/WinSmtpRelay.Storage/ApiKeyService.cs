using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class ApiKeyService(RelayDbContext db) : IApiKeyService
{
    private const string Prefix = "wsr_";
    private const int PrefixStoreLength = 12; // "wsr_" + 8 chars, enough to narrow lookups

    public async Task<IReadOnlyList<ApiKey>> GetAllAsync(int? tenantId, CancellationToken cancellationToken)
    {
        var query = db.ApiKeys.AsNoTracking();
        if (tenantId is not null)
            query = query.Where(k => k.TenantId == tenantId);

        return await query.OrderByDescending(k => k.CreatedUtc).ToListAsync(cancellationToken);
    }

    public async Task<(ApiKey Key, string Plaintext)> CreateAsync(
        int? tenantId, string name, string role, DateTime? expiresUtc, CancellationToken cancellationToken)
    {
        var plaintext = GenerateKey();
        var entity = new ApiKey
        {
            TenantId = tenantId,
            Name = name,
            Role = role,
            KeyPrefix = plaintext[..PrefixStoreLength],
            KeyHash = Hash(plaintext),
            IsEnabled = true,
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = expiresUtc
        };

        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return (entity, plaintext);
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
        var now = DateTime.UtcNow;

        foreach (var candidate in candidates)
        {
            var candidateHashBytes = Encoding.ASCII.GetBytes(candidate.KeyHash);
            if (candidateHashBytes.Length == presentedHashBytes.Length &&
                CryptographicOperations.FixedTimeEquals(candidateHashBytes, presentedHashBytes))
            {
                if (!candidate.IsEnabled || (candidate.ExpiresUtc is not null && candidate.ExpiresUtc <= now))
                    return null;

                candidate.LastUsedUtc = now;
                await db.SaveChangesAsync(cancellationToken);
                return candidate;
            }
        }

        return null;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await db.ApiKeys.Where(k => k.Id == id).ExecuteDeleteAsync(cancellationToken);
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

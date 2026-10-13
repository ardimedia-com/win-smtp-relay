using System.Text.RegularExpressions;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.AdminApi;

/// <summary>
/// The safe DKIM flow shared by REST (<c>/api/dkim/keys</c>) and MCP: the key is generated and stored in the
/// database, the caller gets only the DNS record to publish (never the private key), a new key starts DISABLED,
/// and enabling it requires the public TXT record to match. It never touches <c>PrivateKeyPath</c> (an arbitrary
/// host path), which is why tenant-scoped callers may use it for their own sender domains — the raw
/// <c>/api/dkim/domains</c> endpoints stay host-only.
/// </summary>
public static partial class DkimKeyOperations
{
    public static readonly int[] AllowedKeySizes = [2048, 3072, 4096];

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex SelectorPattern();

    /// <summary>The DKIM keys of the caller's scope (tenant filter applies), with the DNS record each one needs.</summary>
    public static async Task<IReadOnlyList<DkimKeyInfo>> ListAsync(IDkimDomainService dkim, CancellationToken ct) =>
        [.. (await dkim.GetAllAsync(ct)).Select(DkimKeyInfo.From)];

    /// <summary>
    /// Generates a key for an accepted sender domain of the caller's scope and stores it disabled. An existing key
    /// for the domain is replaced only with <paramref name="replace"/> — the replacement is disabled too, so the
    /// domain is unsigned until the new record is published and the key enabled.
    /// </summary>
    public static async Task<DkimKeyInfo> GenerateAsync(
        string domain, string? selector, int? keySize, bool replace,
        IDkimDomainService dkim, IAcceptedSenderDomainService senders, CancellationToken ct)
    {
        domain = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length == 0)
            throw new InvalidOperationException("Domain must not be empty.");

        selector = string.IsNullOrWhiteSpace(selector) ? $"wsr{DateTimeOffset.UtcNow:yyyyMM}" : selector.Trim().ToLowerInvariant();
        if (!SelectorPattern().IsMatch(selector))
            throw new InvalidOperationException($"'{selector}' is not a valid selector (letters, digits and hyphens, e.g. 'wsr202610').");

        var size = keySize ?? 2048;
        if (!AllowedKeySizes.Contains(size))
            throw new InvalidOperationException($"Key size must be one of {string.Join(", ", AllowedKeySizes)} bits.");

        // The tenant query filter makes this the authorization check: a tenant caller only sees its own domains.
        var sender = (await senders.GetAllAsync(ct)).FirstOrDefault(s => s.Domain == domain)
            ?? throw new InvalidOperationException(
                $"'{domain}' is not an accepted sender domain in this scope — add it first (add_accepted_sender_domain).");

        var existing = (await dkim.GetAllAsync(ct)).FirstOrDefault(d => d.Domain == domain && d.TenantId == sender.TenantId);
        if (existing is not null && !replace)
            throw new InvalidOperationException(
                $"'{domain}' already has a DKIM key (selector '{existing.Selector}', {(existing.IsEnabled ? "enabled" : "disabled")}). "
                + "Pass replace=true to generate a new one — it starts disabled, so the domain is unsigned until the new record is published and enabled.");

        var (privateKeyPem, _, _) = DkimKeyGenerator.GenerateKeyPair(domain, selector, size);

        if (existing is null)
        {
            existing = await dkim.CreateAsync(new DkimDomain
            {
                TenantId = sender.TenantId,
                Domain = domain,
                Selector = selector,
                PrivateKeyPem = privateKeyPem,
                IsEnabled = false,
            }, ct);
        }
        else
        {
            existing.Selector = selector;
            existing.PrivateKeyPem = privateKeyPem;
            existing.PrivateKeyPath = null;
            existing.IsEnabled = false;
            await dkim.UpdateAsync(existing, ct);
        }

        return DkimKeyInfo.From(existing);
    }

    /// <summary>
    /// Enables or disables signing with a domain's key. Enabling first checks that the public TXT record at
    /// <c>selector._domainkey.domain</c> carries this key — otherwise every signed message would fail DKIM.
    /// </summary>
    public static async Task<DkimKeyInfo> SetEnabledAsync(
        string domain, bool enabled, IDkimDomainService dkim, IDnsSetupService dns, CancellationToken ct)
    {
        domain = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        var key = (await dkim.GetAllAsync(ct)).FirstOrDefault(d => d.Domain == domain)
            ?? throw new KeyNotFoundException($"No DKIM key for '{domain}' in this scope.");

        if (enabled && !key.IsEnabled && !await dns.IsDkimKeyPublishedAsync(key, ct))
            throw new InvalidOperationException(
                $"Not enabled: the public DNS record {key.Selector}._domainkey.{domain} does not carry this key yet. "
                + "Publish the TXT value from generate_dkim_key / list_dkim_domains, wait for DNS to propagate, then try again.");

        if (key.IsEnabled != enabled)
        {
            key.IsEnabled = enabled;
            await dkim.UpdateAsync(key, ct);
        }
        return DkimKeyInfo.From(key);
    }
}

/// <summary>A DKIM key as an API caller sees it: the record to publish, never the private key.</summary>
public record DkimKeyInfo(
    int Id, int TenantId, string Domain, string Selector, bool IsEnabled, string DnsName, string? DnsTxtValue,
    DateTimeOffset CreatedUtc)
{
    public static DkimKeyInfo From(DkimDomain d)
    {
        string? txt = null;
        if (!string.IsNullOrWhiteSpace(d.PrivateKeyPem))
        {
            try { txt = DkimKeyGenerator.DeriveDnsTxtValue(d.PrivateKeyPem); }
            catch { /* unreadable key: the record cannot be shown, the DNS check reports it */ }
        }
        return new(d.Id, d.TenantId, d.Domain, d.Selector, d.IsEnabled, $"{d.Selector}._domainkey.{d.Domain}", txt, d.CreatedUtc);
    }
}

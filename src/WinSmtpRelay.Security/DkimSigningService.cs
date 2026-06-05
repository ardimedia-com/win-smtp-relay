using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Security;

public class DkimSigningService
{
    private readonly DkimOptions _options;
    private readonly Dictionary<string, DkimSigner> _signers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DkimDomainConfig> _domainConfigs = new(StringComparer.OrdinalIgnoreCase);
    // Per-(domain|selector|keypath) signers built from tenant DB keys.
    private readonly ConcurrentDictionary<string, DkimSigner> _dbSigners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<DkimSigningService> _logger;

    private static readonly HeaderId[] HeadersToSign =
    [
        HeaderId.From,
        HeaderId.To,
        HeaderId.Subject,
        HeaderId.Date,
        HeaderId.MessageId
    ];

    public DkimSigningService(IOptions<DkimOptions> options, ILogger<DkimSigningService> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!_options.Enabled)
            return;

        foreach (var domain in _options.Domains)
        {
            try
            {
                if (!File.Exists(domain.PrivateKeyPath))
                {
                    _logger.LogError("DKIM private key not found for domain {Domain}: {Path}",
                        domain.Domain, domain.PrivateKeyPath);
                    continue;
                }

                var privateKey = LoadPrivateKey(domain.PrivateKeyPath);
                var signer = new DkimSigner(privateKey, domain.Domain, domain.Selector)
                {
                    HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                    BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed
                };

                _signers[domain.Domain] = signer;
                _domainConfigs[domain.Domain] = domain;

                _logger.LogInformation("DKIM signing configured for {Domain} (selector={Selector})",
                    domain.Domain, domain.Selector);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load DKIM key for domain {Domain}", domain.Domain);
            }
        }
    }

    public bool IsConfiguredForDomain(string domain) => _signers.ContainsKey(domain);

    /// <summary>
    /// Signs a message for a specific tenant. <paramref name="tenantDkim"/> is the tenant's DKIM key
    /// for the sender domain (resolved by the delivery layer with an explicit tenant filter), so a
    /// tenant can never sign with another tenant's key. Falls back to the legacy config-based signer
    /// only for the default tenant.
    /// </summary>
    public void Sign(MimeMessage message, int tenantId, DkimDomain? tenantDkim)
    {
        var senderDomain = message.From.Mailboxes.FirstOrDefault()?.Domain;
        if (senderDomain == null)
            return;

        DkimSigner? signer = null;
        if (tenantDkim is not null)
            signer = GetOrBuildDbSigner(tenantDkim);
        else if (tenantId == TenantDefaults.DefaultTenantId && _options.Enabled)
            _signers.TryGetValue(senderDomain, out signer);

        if (signer is null)
            return;

        try
        {
            signer.Sign(message, HeadersToSign);
            _logger.LogDebug("DKIM signed message from {Domain} (tenant {TenantId})", senderDomain, tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DKIM signing failed for message from {Domain}", senderDomain);
        }
    }

    private DkimSigner? GetOrBuildDbSigner(DkimDomain dkim)
    {
        // Prefer the DB-stored PEM (keeps key material off the host filesystem); fall back to a legacy
        // on-disk path. Cache by the key source so a regenerated key produces a fresh signer.
        var source = !string.IsNullOrWhiteSpace(dkim.PrivateKeyPem) ? dkim.PrivateKeyPem : dkim.PrivateKeyPath;
        if (string.IsNullOrWhiteSpace(source))
        {
            _logger.LogWarning("DKIM domain {Domain} has neither a stored key nor a key path", dkim.Domain);
            return null;
        }

        var cacheKey = $"{dkim.Domain}|{dkim.Selector}|{source}";
        if (_dbSigners.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            AsymmetricKeyParameter privateKey;
            if (!string.IsNullOrWhiteSpace(dkim.PrivateKeyPem))
            {
                privateKey = ParsePrivateKeyPem(dkim.PrivateKeyPem);
            }
            else if (File.Exists(dkim.PrivateKeyPath!))
            {
                privateKey = ParsePrivateKeyPem(File.ReadAllText(dkim.PrivateKeyPath!));
            }
            else
            {
                _logger.LogWarning("DKIM private key file not found for {Domain}: {Path}", dkim.Domain, dkim.PrivateKeyPath);
                return null; // not cached, so a later-created key is picked up
            }

            var signer = new DkimSigner(privateKey, dkim.Domain, dkim.Selector)
            {
                HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed
            };
            _dbSigners[cacheKey] = signer;
            return signer;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load DKIM key for {Domain}", dkim.Domain);
            return null;
        }
    }

    private static AsymmetricKeyParameter LoadPrivateKey(string path)
        => ParsePrivateKeyPem(File.ReadAllText(path));

    private static AsymmetricKeyParameter ParsePrivateKeyPem(string pem)
    {
        using var reader = new StringReader(pem);
        var pemReader = new PemReader(reader);
        var keyObject = pemReader.ReadObject();

        return keyObject switch
        {
            AsymmetricCipherKeyPair keyPair => keyPair.Private,
            AsymmetricKeyParameter key => key,
            _ => throw new InvalidOperationException($"Unexpected key type: {keyObject?.GetType()}")
        };
    }
}

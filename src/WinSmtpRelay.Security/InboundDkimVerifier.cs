using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using WinSmtpRelay.Security.Models;

namespace WinSmtpRelay.Security;

/// <summary>
/// Verifies the DKIM signature(s) on a received message so DMARC can use the DKIM-alignment pass path.
/// Public keys are fetched from DNS (<c>selector._domainkey.domain</c>) via <see cref="DnsDkimPublicKeyLocator"/>.
/// Cryptographic verification is delegated to MimeKit's <see cref="DkimVerifier"/>.
/// </summary>
public sealed class InboundDkimVerifier
{
    private readonly DkimVerifier _verifier;
    private readonly ILogger<InboundDkimVerifier> _logger;

    public InboundDkimVerifier(ILookupClient dns, ILogger<InboundDkimVerifier> logger)
    {
        _verifier = new DkimVerifier(new DnsDkimPublicKeyLocator(dns));
        _logger = logger;
    }

    public async Task<DkimCheckResult> VerifyAsync(MimeMessage message, CancellationToken ct = default)
    {
        var signatures = message.Headers.Where(h => h.Id == HeaderId.DkimSignature).ToList();
        if (signatures.Count == 0)
            return new DkimCheckResult(DkimVerdict.None, null, "no DKIM signature");

        DkimCheckResult? lastNonPass = null;
        foreach (var sig in signatures)
        {
            var domain = ParseTag(sig.Value, 'd');
            try
            {
                if (await _verifier.VerifyAsync(message, sig, ct))
                    return new DkimCheckResult(DkimVerdict.Pass, domain, $"valid signature (d={domain})");
                lastNonPass = new DkimCheckResult(DkimVerdict.Fail, domain, $"signature did not verify (d={domain})");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DKIM verification error for d={Domain}", domain);
                lastNonPass = new DkimCheckResult(DkimVerdict.TempError, domain, $"verification error: {ex.Message}");
            }
        }

        return lastNonPass ?? new DkimCheckResult(DkimVerdict.Fail, null, "no valid signature");
    }

    /// <summary>Reads a single tag value (e.g. <c>d</c>, <c>s</c>) from a DKIM-Signature header value.</summary>
    private static string? ParseTag(string headerValue, char tag)
    {
        foreach (var part in headerValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            if (part[..eq].Trim().Equals(tag.ToString(), StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return null;
    }
}

/// <summary>Fetches DKIM public keys from DNS TXT records for MimeKit's verifier.</summary>
internal sealed class DnsDkimPublicKeyLocator(ILookupClient dns) : DkimPublicKeyLocatorBase
{
    public override AsymmetricKeyParameter LocatePublicKey(string methods, string domain, string selector, CancellationToken cancellationToken = default)
        => LocatePublicKeyAsync(methods, domain, selector, cancellationToken).GetAwaiter().GetResult();

    public override async Task<AsymmetricKeyParameter> LocatePublicKeyAsync(
        string methods, string domain, string selector, CancellationToken cancellationToken = default)
    {
        var name = $"{selector}._domainkey.{domain}";
        var result = await dns.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);
        var txt = result.Answers
            .OfType<TxtRecord>()
            .Select(t => string.Join("", t.Text))
            .FirstOrDefault(t => t.Contains("p=", StringComparison.OrdinalIgnoreCase));

        if (txt is null)
            throw new InvalidOperationException($"no DKIM public key published at {name}");

        return GetPublicKey(txt);
    }
}

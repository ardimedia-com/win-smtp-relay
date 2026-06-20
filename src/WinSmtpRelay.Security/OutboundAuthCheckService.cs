using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Utils;
using Org.BouncyCastle.Crypto;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Security;

/// <summary>
/// Local outbound authentication self-test: builds the exact message the relay would send for a From
/// address, DKIM-signs it through the real signing path, then verifies the produced signature against the
/// relay's OWN key (no DNS round-trip) and combines that with the published-DNS DMARC synthesis. Proves
/// end-to-end that signing works and is aligned, without sending mail or depending on an external verifier.
/// </summary>
public sealed class OutboundAuthCheckService(
    DkimSigningService dkimSigner,
    IDkimDomainService dkimDomains,
    IDnsSetupService dnsSetup,
    IPublicSuffixService psl,
    ILogger<OutboundAuthCheckService> logger) : IOutboundAuthCheckService
{
    public async Task<OutboundAuthCheck> CheckAsync(int tenantId, string fromAddress, CancellationToken ct = default)
    {
        fromAddress = fromAddress.Trim();
        var domain = EnvelopeAlignment.DomainOf(fromAddress) ?? "";
        var notes = new List<string>();

        // The DNS-derived synthesis (SPF/DKIM/DMARC published state) for this domain.
        var dnsSetupResult = await dnsSetup.CheckDomainAsync(domain, ct);
        var alignment = dnsSetupResult.Alignment;

        var tenantDkim = string.IsNullOrEmpty(domain)
            ? null
            : await dkimDomains.GetForSigningAsync(tenantId, domain, ct);

        var dkimSigned = false;
        var dkimValid = false;
        string? signingDomain = null;
        var dkimAligned = false;

        if (tenantDkim is null)
        {
            notes.Add($"No enabled DKIM key is configured for {domain} — the relay would send this mail unsigned. "
                + "Set up DKIM so DMARC passes regardless of the sending app's envelope-from.");
        }
        else
        {
            var message = BuildTestMessage(fromAddress, domain);
            dkimSigner.Sign(message, tenantId, tenantDkim);

            var signatureHeader = message.Headers.LastOrDefault(h => h.Id == HeaderId.DkimSignature);
            dkimSigned = signatureHeader is not null;
            if (!dkimSigned)
            {
                notes.Add("A DKIM key is configured but no signature was produced — check the key is valid and enabled.");
            }
            else
            {
                signingDomain = ParseTag(signatureHeader!.Value, 'd');
                dkimAligned = EnvelopeAlignment.IsAligned(signingDomain, domain, psl);
                dkimValid = await VerifyLocallyAsync(message, signatureHeader, tenantDkim, ct);

                notes.Add(dkimValid
                    ? $"DKIM signature is valid and verified against the relay's own key (d={signingDomain})."
                    : "DKIM signature was produced but did NOT verify against the relay's own key — the key material may be inconsistent.");
                if (dkimSigned && !dkimAligned)
                    notes.Add($"DKIM signing domain (d={signingDomain}) does not align with the From domain {domain}.");
            }
        }

        // Envelope-from alignment note: the local test uses an aligned envelope-from, but real submissions
        // are controlled by the sending app — call out the dependency when DKIM doesn't already cover it.
        if (!(dkimSigned && dkimValid && dkimAligned))
        {
            notes.Add(alignment.SpfAuthorized
                ? "SPF authorises this relay, but DMARC then depends on the sending app using a Return-Path on this domain (SPF alignment)."
                : "SPF does not yet authorise this relay for this domain — publish the recommended SPF record.");
        }

        notes.Add(alignment.Summary);

        return new OutboundAuthCheck(
            fromAddress, domain, dkimSigned, dkimValid, signingDomain, dkimAligned, alignment, notes);
    }

    private static MimeMessage BuildTestMessage(string fromAddress, string domain)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(fromAddress));
        // Placeholder recipient — the message is never sent, only signed and inspected.
        message.To.Add(new MailboxAddress("", $"auth-selftest@{(string.IsNullOrEmpty(domain) ? "example.com" : domain)}"));
        message.Subject = "WIN-SMTP-RELAY authentication self-test";
        message.Date = DateTimeOffset.UtcNow;
        message.MessageId = MimeUtils.GenerateMessageId();
        message.Body = new TextPart("plain")
        {
            Text = "Local authentication self-test message. Not sent — built and signed to verify DKIM/DMARC alignment."
        };
        return message;
    }

    /// <summary>Verifies the produced signature against the public key derived from the configured private key (no DNS).</summary>
    private async Task<bool> VerifyLocallyAsync(MimeMessage message, Header signatureHeader, DkimDomain dkim, CancellationToken ct)
    {
        try
        {
            var publicKeyTxt = DeriveDkimTxt(dkim);
            if (publicKeyTxt is null)
                return false;
            var verifier = new DkimVerifier(new StaticDkimPublicKeyLocator(publicKeyTxt));
            return await verifier.VerifyAsync(message, signatureHeader, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local DKIM self-verification failed for {Domain}", dkim.Domain);
            return false;
        }
    }

    /// <summary>Derives "v=DKIM1; k=rsa; p=&lt;SubjectPublicKeyInfo base64&gt;" from the configured private key.</summary>
    private static string? DeriveDkimTxt(DkimDomain dkim)
    {
        string pem;
        if (!string.IsNullOrWhiteSpace(dkim.PrivateKeyPem))
            pem = dkim.PrivateKeyPem;
        else if (!string.IsNullOrWhiteSpace(dkim.PrivateKeyPath) && File.Exists(dkim.PrivateKeyPath))
            pem = File.ReadAllText(dkim.PrivateKeyPath);
        else
            return null;

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

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

/// <summary>A DKIM public-key locator that returns one fixed key TXT for any lookup — used to verify a message against the relay's own key without a DNS round-trip.</summary>
internal sealed class StaticDkimPublicKeyLocator(string publicKeyTxt) : DkimPublicKeyLocatorBase
{
    public override AsymmetricKeyParameter LocatePublicKey(string methods, string domain, string selector, CancellationToken cancellationToken = default)
        => GetPublicKey(publicKeyTxt);

    public override Task<AsymmetricKeyParameter> LocatePublicKeyAsync(
        string methods, string domain, string selector, CancellationToken cancellationToken = default)
        => Task.FromResult(GetPublicKey(publicKeyTxt));
}

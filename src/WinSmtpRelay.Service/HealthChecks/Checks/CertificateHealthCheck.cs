using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// Certificate health: the inbound SMTP-listener TLS certificate (if configured) and the admin-UI HTTPS
/// certificate — that they load and aren't expired or about to expire. An expired listener certificate
/// breaks STARTTLS for inbound mail; an expired admin certificate breaks the management UI.
/// </summary>
public sealed class CertificateHealthCheck(
    CertificateLoader listenerCertLoader,
    IOptions<TlsOptions> tlsOptions,
    IAdminCertificateProvider adminCert,
    IOptions<HealthCheckOptions> options,
    ILogger<CertificateHealthCheck> logger) : HealthCheckBase
{
    public override string Name => "Certificates";
    protected override string Category => HealthCategories.Certificates;

    public override Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct)
    {
        var f = new List<HealthFinding>();
        var tls = tlsOptions.Value;

        // --- Inbound SMTP listener TLS certificate ---
        var listenerConfigured = !string.IsNullOrWhiteSpace(tls.CertificatePath) || !string.IsNullOrWhiteSpace(tls.CertificateThumbprint);
        if (!listenerConfigured)
        {
            f.Add(Info("listener-cert", "No inbound TLS certificate configured",
                "No certificate is set for the SMTP listener, so inbound STARTTLS is unavailable. Configure Tls:CertificatePath or Tls:CertificateThumbprint to let senders use TLS."));
        }
        else
        {
            try
            {
                var cert = listenerCertLoader.LoadCertificate();
                if (cert is null)
                    f.Add(Err("listener-cert", "Inbound TLS certificate could not be loaded",
                        "A certificate is configured for the SMTP listener but it could not be loaded (missing file, wrong password, or thumbprint not found). Inbound STARTTLS will fail.",
                        hint: "Check Tls:CertificatePath/CertificatePassword or Tls:CertificateThumbprint."));
                else
                    f.Add(Expiry("listener-cert", "Inbound TLS certificate", cert));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "loading listener certificate failed");
                f.Add(Err("listener-cert", "Inbound TLS certificate could not be loaded", $"Loading the SMTP listener certificate threw an error: {ex.Message}"));
            }
        }

        // --- Admin UI HTTPS certificate ---
        var current = adminCert.Current;
        if (current is null)
            f.Add(Info("admin-cert", "Admin UI HTTPS is not active",
                "No certificate is being served for the admin UI (HTTPS disabled or unavailable). The management plane is loopback/HTTP only."));
        else
            f.Add(Expiry("admin-cert", "Admin UI HTTPS certificate", current));

        return Task.FromResult<IReadOnlyList<HealthFinding>>(f);
    }

    private HealthFinding Expiry(string code, string label, X509Certificate2 cert)
    {
        var opts = options.Value;
        var subject = string.IsNullOrWhiteSpace(cert.Subject) ? "(certificate)" : cert.Subject;
        var notAfterUtc = cert.NotAfter.ToUniversalTime();
        var days = (notAfterUtc - DateTime.UtcNow).TotalDays;
        var until = $"valid until {notAfterUtc:yyyy-MM-dd HH:mm} UTC";

        if (days < 0)
            return Err(code, $"{label} has expired", $"{subject} expired on {notAfterUtc:yyyy-MM-dd}.", subject, "Install a current certificate.");
        if (days <= opts.CertExpiryErrorDays)
            return Err(code, $"{label} expires in {days:F0} day(s)", $"{subject} — {until}. Renew it now to avoid an outage.", subject, "Renew/replace the certificate.");
        if (days <= opts.CertExpiryWarningDays)
            return Warn(code, $"{label} expires in {days:F0} day(s)", $"{subject} — {until}. Plan to renew it.", subject, "Renew/replace the certificate soon.");
        return Ok(code, $"{label} is valid", $"{subject} — {until}.", subject);
    }
}

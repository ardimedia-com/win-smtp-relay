using System.Security.Cryptography.X509Certificates;
using SmtpServer;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// Hands the SMTP library the CURRENT certificate on every TLS handshake (implicit TLS and STARTTLS), so a
/// renewed certificate is served without restarting the service. <see cref="CertificateLoader"/> caches it
/// and re-checks the source at most once a minute.
/// </summary>
internal sealed class ReloadingCertificateFactory(CertificateLoader loader) : ICertificateFactory
{
    public X509Certificate GetServerCertificate(ISessionContext sessionContext) =>
        loader.LoadCertificate()
        ?? throw new InvalidOperationException("No TLS certificate is available for the SMTP listener.");
}

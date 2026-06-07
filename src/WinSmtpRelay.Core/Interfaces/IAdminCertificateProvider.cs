using System.Security.Cryptography.X509Certificates;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Holds the certificate the admin-UI HTTPS endpoint currently serves. Kestrel reads <see cref="Current"/>
/// per TLS handshake (via a ServerCertificateSelector), so importing a new certificate through the admin
/// UI takes effect on new connections without restarting the service.
/// </summary>
public interface IAdminCertificateProvider
{
    /// <summary>The certificate served for new HTTPS connections, or <c>null</c> if HTTPS is not active.</summary>
    X509Certificate2? Current { get; }

    /// <summary>Swaps the served certificate. New connections use it immediately; existing ones are unaffected.</summary>
    void Set(X509Certificate2 certificate);
}

using System.Security.Cryptography.X509Certificates;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Core;

/// <summary>
/// Thread-safe holder for the admin-UI HTTPS certificate. Registered as a singleton; Kestrel's
/// ServerCertificateSelector reads <see cref="Current"/> on each handshake, and the admin UI calls
/// <see cref="Set"/> after importing a certificate so the swap takes effect without a restart.
/// </summary>
public sealed class AdminCertificateProvider : IAdminCertificateProvider
{
    private volatile X509Certificate2? _current;

    public X509Certificate2? Current => _current;

    public void Set(X509Certificate2 certificate) => _current = certificate;
}

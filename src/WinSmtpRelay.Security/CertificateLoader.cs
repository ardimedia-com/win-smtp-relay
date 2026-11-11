using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;

namespace WinSmtpRelay.Security;

/// <summary>
/// Supplies the SMTP listener's TLS certificate — a PFX file or a LocalMachine\My thumbprint — and keeps it
/// current while the service runs.
/// <para>
/// The certificate is cached and its source re-checked at most once per <see cref="RecheckInterval"/>: a
/// renewed PFX dropped over the old file (or a certificate imported into the store after start) is picked
/// up by the next TLS handshake without a restart. A source that has become unreadable keeps the last good
/// certificate in service and logs an error, rather than failing every handshake.
/// </para>
/// <para>
/// A PFX is loaded into a NON-persisted machine key container: Windows SChannel cannot serve TLS from an
/// ephemeral key, but without <c>PersistKeySet</c> the container file under MachineKeys is deleted when
/// the certificate is disposed. The previous loader persisted it, which left one orphaned key file per
/// start (and per health-check run). A replaced certificate is disposed only after a grace period, so a
/// handshake still using it is never cut off.
/// </para>
/// </summary>
public class CertificateLoader : IDisposable
{
    /// <summary>How often the configured source is checked for a changed certificate.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>A certificate that expires sooner than this is logged as a warning (once a day).</summary>
    public const int ExpiryWarningDays = 21;

    /// <summary>How long a replaced certificate stays alive for handshakes that already hold it.</summary>
    private static readonly TimeSpan RetiredGracePeriod = TimeSpan.FromMinutes(10);

    private readonly TlsOptions _options;
    private readonly ILogger<CertificateLoader> _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<(X509Certificate2 Certificate, DateTimeOffset DisposeAfter)> _retired = [];

    private X509Certificate2? _current;
    private string? _currentStamp;

    /// <summary>The source state last attempted — a failing or missing source is reported once, not every minute.</summary>
    private string? _attemptedStamp;
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _nextExpiryLog = DateTimeOffset.MinValue;
    private bool _disposed;

    public CertificateLoader(IOptions<TlsOptions> options, ILogger<CertificateLoader> logger, TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True when a certificate source (PFX path or thumbprint) is configured at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.CertificatePath) || !string.IsNullOrWhiteSpace(_options.CertificateThumbprint);

    /// <summary>
    /// The current certificate, or null when none is configured or none could ever be loaded. The returned
    /// instance is owned by this loader — callers must not dispose it.
    /// </summary>
    public X509Certificate2? LoadCertificate()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var now = _time.GetUtcNow();
            DisposeRetired(now);

            if (now >= _nextCheck)
            {
                _nextCheck = now + RecheckInterval;
                Refresh();
            }

            if (_current is not null && now >= _nextExpiryLog)
            {
                _nextExpiryLog = now + TimeSpan.FromDays(1);
                LogExpiry(_current, now);
            }

            return _current;
        }
    }

    private void Refresh()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.CertificatePath))
                RefreshFromFile(_options.CertificatePath);
            else if (!string.IsNullOrWhiteSpace(_options.CertificateThumbprint))
                RefreshFromStore(_options.CertificateThumbprint);
            else if (_currentStamp is null)
            {
                _currentStamp = "none";
                _logger.LogDebug("No TLS certificate configured");
            }
        }
        catch (Exception ex)
        {
            // A wrong password or a corrupt file must not take the listener down: keep the last good one.
            _logger.LogError(ex, "Loading the SMTP TLS certificate failed{Fallback}",
                _current is null ? "" : "; the previously loaded certificate stays in service");
        }
    }

    private void RefreshFromFile(string path)
    {
        if (!File.Exists(path))
        {
            if (!TryBeginAttempt($"missing|{path}"))
                return;
            _logger.LogError("Certificate file not found: {Path}{Fallback}", path,
                _current is null ? "" : "; the previously loaded certificate stays in service");
            return;
        }

        var info = new FileInfo(path);
        var stamp = $"file|{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        if (stamp == _currentStamp || !TryBeginAttempt(stamp))
            return;

        var cert = X509CertificateLoader.LoadPkcs12FromFile(path, _options.CertificatePassword, X509KeyStorageFlags.MachineKeySet);
        Replace(cert, stamp, "PFX");
    }

    private void RefreshFromStore(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        try
        {
            if (found.Count == 0)
            {
                if (!TryBeginAttempt($"missing|{thumbprint}"))
                    return;
                _logger.LogError("Certificate with thumbprint {Thumbprint} not found in LocalMachine\\My{Fallback}", thumbprint,
                    _current is null ? "" : "; the previously loaded certificate stays in service");
                return;
            }

            var stamp = $"store|{found[0].Thumbprint}";
            if (stamp == _currentStamp)
                return;
            _attemptedStamp = stamp;

            var cert = found[0];
            found.Remove(cert); // ownership moves to the cache; the rest of the collection is disposed below
            Replace(cert, stamp, "store");
        }
        finally
        {
            foreach (var c in found)
                c.Dispose();
        }
    }

    private bool TryBeginAttempt(string stamp)
    {
        if (stamp == _attemptedStamp)
            return false;
        _attemptedStamp = stamp;
        return true;
    }

    private void Replace(X509Certificate2 cert, string stamp, string source)
    {
        if (_current is not null)
            _retired.Add((_current, _time.GetUtcNow() + RetiredGracePeriod));

        var reloaded = _currentStamp is not null && _currentStamp != "none";
        _current = cert;
        _currentStamp = stamp;
        _nextExpiryLog = DateTimeOffset.MinValue;

        _logger.LogInformation("{Action} TLS certificate from {Source}: {Subject} (expires {Expiry:yyyy-MM-dd})",
            reloaded ? "Reloaded" : "Loaded", source, cert.Subject, cert.NotAfter.ToUniversalTime());
    }

    private void LogExpiry(X509Certificate2 cert, DateTimeOffset now)
    {
        var notAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var remaining = notAfter - now;

        if (remaining <= TimeSpan.Zero)
            _logger.LogError("The SMTP TLS certificate {Subject} expired on {Expiry:yyyy-MM-dd}; senders that validate certificates will refuse STARTTLS",
                cert.Subject, notAfter);
        else if (remaining < TimeSpan.FromDays(ExpiryWarningDays))
            _logger.LogWarning("The SMTP TLS certificate {Subject} expires in {Days} day(s), on {Expiry:yyyy-MM-dd}",
                cert.Subject, (int)remaining.TotalDays, notAfter);
    }

    private void DisposeRetired(DateTimeOffset now)
    {
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (now < _retired[i].DisposeAfter)
                continue;

            _retired[i].Certificate.Dispose();
            _retired.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var (cert, _) in _retired)
                cert.Dispose();
            _retired.Clear();
            _current?.Dispose();
            _current = null;
        }

        GC.SuppressFinalize(this);
    }
}

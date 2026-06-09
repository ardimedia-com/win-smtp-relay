namespace WinSmtpRelay.Core.Models;

/// <summary>
/// A certificate found in the Windows <c>LocalMachine\My</c> store, offered as a candidate for the
/// admin-UI HTTPS endpoint. Only certificates that have a private key and a server-authentication
/// purpose are surfaced. <see cref="IsTrusted"/> indicates the certificate chains to a trusted root and
/// is currently valid — i.e. a browser would accept it (a self-signed/untrusted one would still warn).
/// </summary>
public sealed record StoreCertificateInfo(
    string Thumbprint, string Subject, string Issuer, DateTimeOffset NotAfterUtc, bool IsTrusted);

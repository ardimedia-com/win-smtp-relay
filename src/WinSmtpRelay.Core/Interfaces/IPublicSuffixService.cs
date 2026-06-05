namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Resolves the registrable ("organizational") domain of a hostname using the Public Suffix List —
/// e.g. <c>smtp2.ardimedia.com</c> → <c>ardimedia.com</c> and <c>mail.a.b.co.uk</c> → <c>b.co.uk</c>.
/// Lives in Core (interface only) so the Blazor UI can resolve it without referencing the Security
/// implementation (which carries the embedded list).
/// </summary>
public interface IPublicSuffixService
{
    /// <summary>
    /// The registrable domain (the public suffix plus one label), or <c>null</c> when the host is itself a
    /// public suffix, is an IP address, is empty, or has too few labels to have a registrable domain.
    /// </summary>
    string? GetRegistrableDomain(string? host);
}

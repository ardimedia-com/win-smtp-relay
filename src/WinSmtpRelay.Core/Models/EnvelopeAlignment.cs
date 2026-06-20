using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Core.Models;

/// <summary>
/// DMARC SPF-alignment helper: decides whether an envelope-from (MAIL FROM / Return-Path) domain aligns
/// with the header From domain. Relaxed alignment (the DMARC default) compares the registrable
/// ("organizational") domains via the Public Suffix List; strict alignment requires an exact match.
/// Pure domain logic — the registrable-domain lookup is injected as a port.
/// </summary>
public static class EnvelopeAlignment
{
    /// <summary>Extracts the domain part (after the last '@') of an email address, lower-cased; null/empty for a bare or empty value.</summary>
    public static string? DomainOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;
        var at = address.LastIndexOf('@');
        var domain = (at >= 0 ? address[(at + 1)..] : address).Trim().TrimEnd('>').Trim().TrimEnd('.').ToLowerInvariant();
        return domain.Length == 0 ? null : domain;
    }

    /// <summary>
    /// True when <paramref name="envelopeFromDomain"/> aligns with <paramref name="headerFromDomain"/>.
    /// Relaxed (default) compares registrable domains; strict requires an exact domain match.
    /// Returns false if either domain is missing.
    /// </summary>
    public static bool IsAligned(string? envelopeFromDomain, string? headerFromDomain, IPublicSuffixService psl, bool strict = false)
    {
        if (string.IsNullOrWhiteSpace(envelopeFromDomain) || string.IsNullOrWhiteSpace(headerFromDomain))
            return false;

        var env = envelopeFromDomain.Trim().TrimEnd('.').ToLowerInvariant();
        var hdr = headerFromDomain.Trim().TrimEnd('.').ToLowerInvariant();

        if (string.Equals(env, hdr, StringComparison.Ordinal))
            return true;
        if (strict)
            return false;

        var envOrg = psl.GetRegistrableDomain(env) ?? env;
        var hdrOrg = psl.GetRegistrableDomain(hdr) ?? hdr;
        return string.Equals(envOrg, hdrOrg, StringComparison.Ordinal);
    }
}

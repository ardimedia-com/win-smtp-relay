using System.Globalization;
using System.Net;
using WinSmtpRelay.Core;

namespace WinSmtpRelay.AdminUi.Services;

/// <summary>
/// Validation and normalisation of admin form input, applied before anything is saved. Every helper returns a
/// user-facing message instead of silently falling back to a default: an unparsable number must never turn
/// into 0 or the previous value without the operator noticing.
/// </summary>
public static class InputValidation
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    /// <summary>
    /// Normalises a domain name for storage: trimmed, a trailing dot removed, internationalised names converted to
    /// punycode (<c>bücher.ch</c> → <c>xn--bcher-kva.ch</c>), lower-cased, and checked to be a DNS host name.
    /// </summary>
    public static bool TryNormalizeDomain(string? input, out string domain, out string? error)
    {
        domain = "";
        var text = (input ?? "").Trim().TrimEnd('.');
        if (text.Length == 0)
        {
            error = "Enter a domain name.";
            return false;
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(text).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            error = $"'{text}' is not a valid domain name.";
            return false;
        }

        if (ascii.Length > 253 || !ascii.Contains('.') || Uri.CheckHostName(ascii) != UriHostNameType.Dns)
        {
            error = $"'{text}' is not a valid domain name (expected something like example.com).";
            return false;
        }

        domain = ascii;
        error = null;
        return true;
    }

    /// <summary>
    /// Validates an IP address or a network in CIDR notation (<c>10.0.0.5</c>, <c>10.0.0.0/8</c>,
    /// <c>2001:db8::/48</c>) and returns it in canonical form.
    /// </summary>
    public static bool TryNormalizeNetwork(string? input, out string network, out string? error)
    {
        network = "";
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = "Enter an IP address or a network in CIDR notation, e.g. 10.0.0.0/8.";
            return false;
        }

        if (!IpAddressScope.TryParseAddressOrNetwork(text, out var address))
        {
            error = $"'{text}' is not a valid IP address or CIDR network (e.g. 192.168.1.0/24 or 2001:db8::/48).";
            return false;
        }

        var slash = text.IndexOf('/');
        network = slash >= 0
            ? $"{address}/{int.Parse(text[(slash + 1)..], CultureInfo.InvariantCulture)}"
            : address.ToString();
        error = null;
        return true;
    }

    /// <summary>Parses a whole number within <paramref name="min"/>..<paramref name="max"/>; the message names the field.</summary>
    public static bool TryParseInt(string? input, string fieldName, int min, int max, out int value, out string? error)
    {
        value = 0;
        var text = (input ?? "").Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{fieldName}: enter a whole number.";
            return false;
        }

        if (parsed < min || parsed > max)
        {
            error = max == int.MaxValue
                ? $"{fieldName}: must be {min} or more."
                : $"{fieldName}: must be between {min} and {max}.";
            return false;
        }

        value = parsed;
        error = null;
        return true;
    }

    /// <summary>
    /// Parses a size in megabytes and returns it in bytes, capped so the result fits the <see cref="int"/> the
    /// connector stores (2047 MB). Using <see cref="long"/> for the multiplication avoids the silent overflow
    /// a 2048 MB value used to produce.
    /// </summary>
    public static bool TryParseMegabytesToBytes(string? input, string fieldName, int minMb, out int bytes, out string? error)
    {
        const int MaxMb = int.MaxValue / (1024 * 1024); // 2047
        bytes = 0;
        if (!TryParseInt(input, fieldName, minMb, MaxMb, out var mb, out error))
            return false;

        var total = (long)mb * 1024 * 1024;
        bytes = (int)Math.Min(total, int.MaxValue);
        return true;
    }

    /// <summary>
    /// Parses a comma-separated list of retry intervals in minutes (e.g. <c>1, 5, 15, 30, 60</c>): each must be a
    /// whole number from 1 to 10080 (one week), at least one is required. Returns the canonical «1,5,15» form.
    /// </summary>
    public static bool TryParseRetryIntervals(string? input, out string normalized, out string? error)
    {
        normalized = "";
        var parts = (input ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "Retry intervals: enter at least one interval in minutes, e.g. 1, 5, 15, 30, 60.";
            return false;
        }

        var values = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                || minutes < 1 || minutes > 10080)
            {
                error = $"Retry intervals: '{part}' is not a whole number of minutes between 1 and 10080.";
                return false;
            }
            values.Add(minutes);
        }

        normalized = string.Join(',', values);
        error = null;
        return true;
    }

    /// <summary>Validates an e-mail address enough to catch typos (one @, a domain with a dot, no spaces).</summary>
    public static bool IsPlausibleEmail(string? input)
    {
        var text = (input ?? "").Trim();
        var at = text.LastIndexOf('@');
        return at > 0 && at < text.Length - 1 && !text.Any(char.IsWhiteSpace)
            && text.IndexOf('@') == at && text[(at + 1)..].Contains('.');
    }

    /// <summary>True when <paramref name="text"/> parses as an <see cref="IPAddress"/>.</summary>
    public static bool IsIpAddress(string? text) => IPAddress.TryParse((text ?? "").Trim(), out _);
}

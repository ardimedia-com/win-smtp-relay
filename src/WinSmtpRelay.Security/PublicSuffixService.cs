using System.Net;
using System.Reflection;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Security;

/// <summary>
/// Public Suffix List (https://publicsuffix.org/) lookup for the registrable domain of a hostname.
/// Implements the published matching algorithm against an embedded snapshot of the list. Only the
/// ICANN section is used (the PRIVATE section — blogspot.com, *.s3.amazonaws.com, … — is ignored), so
/// a hostname under a hoster's private suffix resolves to the ICANN registrable domain, which is where
/// SPF/DKIM/DMARC for the organization live. Register as a singleton: the list is parsed once.
/// </summary>
public sealed class PublicSuffixService : IPublicSuffixService
{
    private const string ResourceSuffix = "public_suffix_list.dat";

    private readonly HashSet<string> _rules;       // normal rules, e.g. "com", "co.uk"
    private readonly HashSet<string> _wildcards;   // the "*.X" part after the wildcard, e.g. "ck" for "*.ck"
    private readonly HashSet<string> _exceptions;  // "!X" rules without the "!", e.g. "www.ck"

    public PublicSuffixService()
    {
        _rules = new HashSet<string>(StringComparer.Ordinal);
        _wildcards = new HashSet<string>(StringComparer.Ordinal);
        _exceptions = new HashSet<string>(StringComparer.Ordinal);
        Load(ReadEmbeddedList());
    }

    /// <summary>Test/explicit constructor that parses a caller-supplied list (one rule per line).</summary>
    public PublicSuffixService(string listContents)
    {
        _rules = new HashSet<string>(StringComparer.Ordinal);
        _wildcards = new HashSet<string>(StringComparer.Ordinal);
        _exceptions = new HashSet<string>(StringComparer.Ordinal);
        Load(listContents);
    }

    public string? GetRegistrableDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0 || IPAddress.TryParse(normalized, out _))
            return null;

        var labels = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
            return null;

        var suffixLabels = PublicSuffixLabelCount(labels);
        if (labels.Length <= suffixLabels)
            return null; // the host is itself a public suffix — no registrable domain

        // registrable domain = public suffix + the next label to its left
        return string.Join('.', labels[(labels.Length - suffixLabels - 1)..]);
    }

    private int PublicSuffixLabelCount(string[] labels)
    {
        var n = labels.Length;

        // Exception rules win over any other matching rule; use the longest matching exception.
        for (var i = 0; i < n; i++)
        {
            if (_exceptions.Contains(Join(labels, i)))
                return n - i - 1; // exception => public suffix is one label shorter than the matched rule
        }

        // Otherwise the longest matching normal or wildcard rule (scan the longest suffix first).
        for (var i = 0; i < n; i++)
        {
            if (_rules.Contains(Join(labels, i)))
                return n - i;
            if (n - i >= 2 && _wildcards.Contains(Join(labels, i + 1)))
                return n - i;
        }

        // No rule matched: the implicit "*" rule makes the rightmost label the public suffix.
        return 1;
    }

    private static string Join(string[] labels, int start) => string.Join('.', labels[start..]);

    private void Load(string contents)
    {
        var icannOnly = true;
        using var reader = new StringReader(contents);
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            var rule = line.Trim();
            if (rule.Length == 0)
                continue;
            if (rule.StartsWith("//", StringComparison.Ordinal))
            {
                // Stop at the PRIVATE section — we want ICANN registrable domains only.
                if (rule.Contains("===BEGIN PRIVATE DOMAINS===", StringComparison.Ordinal))
                    break;
                continue;
            }
            if (!icannOnly)
                continue;

            // A rule is a single token; lower-case for ASCII matching (IDN U-label rules won't match
            // ASCII hosts, which is acceptable for this use).
            rule = rule.Split(' ', '\t')[0].ToLowerInvariant();
            if (rule.StartsWith('!'))
                _exceptions.Add(rule[1..]);
            else if (rule.StartsWith("*.", StringComparison.Ordinal))
                _wildcards.Add(rule[2..]);
            else
                _rules.Add(rule);
        }
    }

    private static string ReadEmbeddedList()
    {
        var asm = typeof(PublicSuffixService).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded Public Suffix List '{ResourceSuffix}' not found.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

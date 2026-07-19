namespace WinSmtpRelay.Core.Authorization;

/// <summary>
/// Capability scopes for API keys — an additional restriction on top of the key's role, never an
/// extension of it. The admin API is partitioned into five areas; a scope is <c>"{area}:read"</c> or
/// <c>"{area}:write"</c> (write implies read within its area), plus the special
/// <see cref="MessagesBody"/> scope that alone unlocks raw message bodies.
/// <para>
/// A key with NO scopes is read-only: reads pass (role permitting), writes and bodies are denied
/// (owner decision 2026-07-19 — the safe default for keys minted before scopes existed). A key WITH
/// scopes is limited to exactly the listed areas. Cookie-authenticated admins are never scope-checked;
/// scopes exist so a programmatic caller (automation, MCP) can be given less power than its role.
/// </para>
/// </summary>
public static class ApiKeyScopes
{
    /// <summary>Monitoring reads: metrics, statistics, delivery logs, queue status, server info.</summary>
    public const string Diag = "diag";

    /// <summary>Queued-message metadata (list + detail). Raw bodies need <see cref="MessagesBody"/>.</summary>
    public const string Messages = "messages";

    /// <summary>Queue operations: retry and delete of queued messages.</summary>
    public const string Queue = "queue";

    /// <summary>Relay configuration: connectors, domains, IP rules, routes, DKIM, rate limits, filters.</summary>
    public const string Config = "config";

    /// <summary>Administration: tenants, relay users, API keys.</summary>
    public const string Admin = "admin";

    /// <summary>Special scope gating raw message bodies (customer mail content). Never implied.</summary>
    public const string MessagesBody = "messages:body";

    public static readonly string[] Areas = [Diag, Messages, Queue, Config, Admin];

    public static string Read(string area) => area + ":read";
    public static string Write(string area) => area + ":write";

    /// <summary>Parses the stored space-separated scope string into a set. Null/empty → empty set.</summary>
    public static IReadOnlySet<string> Parse(string? scopes)
    {
        if (string.IsNullOrWhiteSpace(scopes))
            return new HashSet<string>();
        return scopes.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();
    }

    /// <summary>Joins a scope set back into the stored form. Empty → null (the read-only default).</summary>
    public static string? Normalize(IEnumerable<string> scopes)
    {
        var set = scopes.Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0)
            .Distinct()
            .Order()
            .ToList();
        return set.Count == 0 ? null : string.Join(' ', set);
    }

    /// <summary>Whether a single scope token is one this version understands.</summary>
    public static bool IsKnown(string scope) =>
        scope == MessagesBody || Areas.Any(a => scope == Read(a) || scope == Write(a));

    /// <summary>Read access to an area: scope-less keys read everything their role allows.</summary>
    public static bool AllowsRead(IReadOnlySet<string> scopes, string area) =>
        scopes.Count == 0 || scopes.Contains(Read(area)) || scopes.Contains(Write(area));

    /// <summary>Write access to an area: always requires the explicit write scope (scope-less = read-only).</summary>
    public static bool AllowsWrite(IReadOnlySet<string> scopes, string area) =>
        scopes.Contains(Write(area));

    /// <summary>Raw message bodies: only the explicit body scope, never implied by messages:read/write.</summary>
    public static bool AllowsBody(IReadOnlySet<string> scopes) =>
        scopes.Contains(MessagesBody);
}

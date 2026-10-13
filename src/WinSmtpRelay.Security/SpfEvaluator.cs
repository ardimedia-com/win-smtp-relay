using System.Net;
using System.Net.Sockets;

namespace WinSmtpRelay.Security;

/// <summary>The DNS answers an SPF evaluation needs — a seam so the evaluator is testable without DNS.</summary>
public interface ISpfDnsResolver
{
    /// <summary>The domain's <c>v=spf1</c> TXT record, or null when it has none.</summary>
    Task<string?> GetSpfRecordAsync(string domain, CancellationToken ct);

    /// <summary>The host's A and AAAA addresses (empty when it does not resolve).</summary>
    Task<IReadOnlyList<IPAddress>> GetAddressesAsync(string host, CancellationToken ct);

    /// <summary>The domain's MX exchange host names (empty when it has none).</summary>
    Task<IReadOnlyList<string>> GetMxHostsAsync(string domain, CancellationToken ct);
}

/// <summary>SPF check_host() results (RFC 7208 §2.6), minus TempError, which the resolver absorbs as "no answer".</summary>
public enum SpfResult { None, Neutral, Pass, Fail, SoftFail, PermError }

/// <summary>
/// Evaluates whether a domain's published SPF record authorises a given sending IP — the question a receiver
/// asks, instead of comparing the record's text with a recommended one. Supports <c>all</c>, <c>ip4</c>,
/// <c>ip6</c>, <c>a</c>, <c>mx</c> (with CIDR suffixes), <c>include</c>, <c>redirect</c> and the RFC 7208 limit
/// of 10 DNS-querying terms. <c>exists</c>, <c>ptr</c> and macros are counted but never match: the relay cannot
/// evaluate them without a real message, so it reports such a record as not proven rather than guess.
/// </summary>
public static class SpfEvaluator
{
    private const int MaxLookups = 10;
    private const int MaxVoidLookups = 2;

    public static async Task<SpfResult> EvaluateAsync(string domain, IPAddress ip, ISpfDnsResolver resolver, CancellationToken ct)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        var state = new State();
        return await CheckHostAsync(domain.Trim().TrimEnd('.').ToLowerInvariant(), ip, resolver, state, ct);
    }

    private sealed class State
    {
        public int Lookups;
        public int VoidLookups;
    }

    private sealed class PermErrorException(string message) : Exception(message);

    private static async Task<SpfResult> CheckHostAsync(
        string domain, IPAddress ip, ISpfDnsResolver resolver, State state, CancellationToken ct)
    {
        var record = await resolver.GetSpfRecordAsync(domain, ct);
        if (record is null)
            return SpfResult.None;

        try
        {
            string? redirect = null;
            foreach (var raw in record.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.Equals("v=spf1", StringComparison.OrdinalIgnoreCase))
                    continue;

                var eq = raw.IndexOf('=');
                var colon = raw.IndexOf(':');
                if (eq > 0 && (colon < 0 || eq < colon))
                {
                    // Modifier. Only redirect= matters; it applies after all mechanisms failed to match.
                    if (raw[..eq].Equals("redirect", StringComparison.OrdinalIgnoreCase))
                        redirect = raw[(eq + 1)..];
                    continue;
                }

                var hasQualifier = raw[0] is '+' or '-' or '~' or '?';
                var qualifier = hasQualifier ? raw[0] : '+';
                var term = hasQualifier ? raw[1..] : raw;

                if (await MatchesAsync(term, domain, ip, resolver, state, ct))
                    return qualifier switch
                    {
                        '-' => SpfResult.Fail,
                        '~' => SpfResult.SoftFail,
                        '?' => SpfResult.Neutral,
                        _ => SpfResult.Pass,
                    };
            }

            if (redirect is not null)
            {
                CountLookup(state);
                if (redirect.Contains('%'))
                    return SpfResult.Neutral; // macro — not evaluable here
                var result = await CheckHostAsync(redirect.TrimEnd('.').ToLowerInvariant(), ip, resolver, state, ct);
                return result == SpfResult.None ? SpfResult.PermError : result;
            }

            return SpfResult.Neutral; // no match, no "all": default result
        }
        catch (PermErrorException)
        {
            return SpfResult.PermError;
        }
    }

    private static async Task<bool> MatchesAsync(
        string term, string domain, IPAddress ip, ISpfDnsResolver resolver, State state, CancellationToken ct)
    {
        var name = new string([.. term.TakeWhile(char.IsLetterOrDigit)]).ToLowerInvariant();
        var rest = term[name.Length..];

        switch (name)
        {
            case "all":
                return true;

            case "ip4":
            case "ip6":
                return rest.StartsWith(':') && NetworkContains(rest[1..], ip);

            case "a":
            case "mx":
            {
                CountLookup(state);
                var (target, v4Prefix, v6Prefix) = ParseDomainSpecWithCidr(rest, domain);
                if (target is null)
                    return false; // macro
                IReadOnlyList<IPAddress> addresses;
                if (name == "a")
                {
                    addresses = await resolver.GetAddressesAsync(target, ct);
                }
                else
                {
                    var hosts = await resolver.GetMxHostsAsync(target, ct);
                    var list = new List<IPAddress>();
                    foreach (var host in hosts.Take(10)) // RFC 7208 §4.6.4: at most 10 MX names
                        list.AddRange(await resolver.GetAddressesAsync(host, ct));
                    addresses = list;
                }
                if (addresses.Count == 0)
                    CountVoidLookup(state);
                return addresses.Any(a => PrefixMatches(a, ip, a.AddressFamily == AddressFamily.InterNetwork ? v4Prefix : v6Prefix));
            }

            case "include":
            {
                CountLookup(state);
                if (!rest.StartsWith(':') || rest.Contains('%'))
                    return false;
                var result = await CheckHostAsync(rest[1..].TrimEnd('.').ToLowerInvariant(), ip, resolver, state, ct);
                return result switch
                {
                    SpfResult.Pass => true,
                    SpfResult.None or SpfResult.PermError => throw new PermErrorException($"include:{rest[1..]} has no usable SPF record"),
                    _ => false,
                };
            }

            case "exists":
            case "ptr":
                CountLookup(state);
                return false; // not evaluable without a message / reverse lookup policy — never assume a match

            default:
                throw new PermErrorException($"unknown mechanism '{term}'");
        }
    }

    private static void CountLookup(State state)
    {
        if (++state.Lookups > MaxLookups)
            throw new PermErrorException("more than 10 DNS-querying terms");
    }

    private static void CountVoidLookup(State state)
    {
        if (++state.VoidLookups > MaxVoidLookups)
            throw new PermErrorException("more than 2 void lookups");
    }

    // "a", "a:host", "a/24", "a:host/24//64", "a//64" → (host, v4 prefix, v6 prefix). Null host = macro.
    private static (string? Target, int V4Prefix, int V6Prefix) ParseDomainSpecWithCidr(string rest, string current)
    {
        var v4 = 32;
        var v6 = 128;
        var spec = rest;

        var dual = spec.IndexOf("//", StringComparison.Ordinal);
        if (dual >= 0)
        {
            if (int.TryParse(spec[(dual + 2)..], out var p6)) v6 = Math.Clamp(p6, 0, 128);
            spec = spec[..dual];
        }
        var single = spec.LastIndexOf('/');
        if (single >= 0)
        {
            if (int.TryParse(spec[(single + 1)..], out var p4)) v4 = Math.Clamp(p4, 0, 32);
            spec = spec[..single];
        }

        string target = spec.StartsWith(':') ? spec[1..] : current;
        if (target.Contains('%'))
            return (null, v4, v6);
        return (target.TrimEnd('.').ToLowerInvariant(), v4, v6);
    }

    private static bool NetworkContains(string network, IPAddress ip)
    {
        var slash = network.IndexOf('/');
        var addressText = slash >= 0 ? network[..slash] : network;
        if (!IPAddress.TryParse(addressText, out var address))
            return false;
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (slash >= 0 && (!int.TryParse(network[(slash + 1)..], out prefix) || prefix < 0 || prefix > max))
            return false;
        return PrefixMatches(address, ip, prefix);
    }

    private static bool PrefixMatches(IPAddress network, IPAddress ip, int prefix)
    {
        if (network.AddressFamily != ip.AddressFamily)
            return false;
        var a = network.GetAddressBytes();
        var b = ip.GetAddressBytes();
        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
            if (a[i] != b[i])
                return false;
        var remaining = prefix % 8;
        if (remaining == 0)
            return true;
        var mask = (byte)(0xFF << (8 - remaining));
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }
}

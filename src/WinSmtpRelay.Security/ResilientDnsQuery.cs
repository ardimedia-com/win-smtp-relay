using System.Net;
using DnsClient;
using Microsoft.Extensions.Logging;

namespace WinSmtpRelay.Security;

/// <summary>
/// A DNS lookup could not be answered — every resolver timed out, failed or refused. Not the same as "no such
/// record": the record may well be published, it just could not be verified.
/// </summary>
public sealed class DnsLookupFailedException(string name, QueryType type, string reason, Exception? inner = null)
    : Exception($"DNS {type} lookup for {name} failed: {reason}", inner)
{
    public string Name { get; } = name;
    public QueryType Type { get; } = type;
    public string Reason { get; } = reason;
}

/// <summary>
/// Deliverability DNS lookups that tell "not published" apart from "could not ask". Queries the public resolvers
/// first (the internet's view, not a split-horizon answer); when they fail (timeout, SERVFAIL, REFUSED, network
/// down) the system resolver is asked instead. A definitive answer — records, an empty answer, or NXDOMAIN — is
/// returned; when no resolver answers, <see cref="DnsLookupFailedException"/> is thrown so the caller reports
/// "could not verify" instead of "record not published".
/// </summary>
public sealed class ResilientDnsQuery(ILookupClient publicResolvers, ILookupClient? systemResolver, ILogger logger)
{
    public Task<IDnsQueryResponse> QueryAsync(string name, QueryType type, CancellationToken ct) =>
        RunAsync(name, type, client => client.QueryAsync(name, type, cancellationToken: ct), ct);

    public Task<IDnsQueryResponse> QueryReverseAsync(IPAddress address, CancellationToken ct) =>
        RunAsync(address.ToString(), QueryType.PTR, client => client.QueryReverseAsync(address, ct), ct);

    private async Task<IDnsQueryResponse> RunAsync(string name, QueryType type,
        Func<ILookupClient, Task<IDnsQueryResponse>> query, CancellationToken ct)
    {
        var (response, publicError) = await TryAsync(publicResolvers, query, ct);
        if (response is not null)
            return response;

        if (systemResolver is not null && !ReferenceEquals(systemResolver, publicResolvers))
        {
            logger.LogInformation("DNS {Type} lookup for {Name} failed on the public resolvers ({Reason}) — retrying with the system resolver",
                type, name, publicError);
            var (fallback, systemError) = await TryAsync(systemResolver, query, ct);
            if (fallback is not null)
                return fallback;
            throw new DnsLookupFailedException(name, type, $"public resolvers: {publicError}; system resolver: {systemError}");
        }

        throw new DnsLookupFailedException(name, type, publicError ?? "no answer");
    }

    private static async Task<(IDnsQueryResponse? Response, string? Error)> TryAsync(ILookupClient client,
        Func<ILookupClient, Task<IDnsQueryResponse>> query, CancellationToken ct)
    {
        try
        {
            var response = await query(client);
            return IsDefinitive(response) ? (response, null) : (null, response.ErrorMessage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DnsResponseException ex)
        {
            return (null, ex.Code == DnsResponseCode.ConnectionTimeout ? "timeout" : $"{ex.Code}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// A response that settles the question: no error (records or an empty answer), or NXDOMAIN (the name does
    /// not exist). SERVFAIL, REFUSED and the like say nothing about the record.
    /// </summary>
    public static bool IsDefinitive(IDnsQueryResponse response) =>
        !response.HasError || response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain;
}

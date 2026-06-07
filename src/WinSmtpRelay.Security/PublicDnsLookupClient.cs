using System.Net;
using DnsClient;

namespace WinSmtpRelay.Security;

/// <summary>
/// A DnsClient <see cref="LookupClient"/> pinned to public resolvers (Google 8.8.8.8, Cloudflare
/// 1.1.1.1). The deliverability checks use this so they see the <em>public</em> DNS view — what the
/// internet resolves — instead of a split-horizon answer from the host's own resolver (e.g. a public
/// hostname that internally resolves to a private 10.x address).
///
/// It is deliberately NOT used for DNS-blocklist (DNSBL) lookups: Spamhaus and similar refuse queries
/// from large public resolvers, so those stay on the host's own resolver (the shared
/// <see cref="ILookupClient"/>).
/// </summary>
public sealed class PublicDnsLookupClient
{
    public ILookupClient Client { get; }

    public PublicDnsLookupClient()
    {
        Client = new LookupClient(new LookupClientOptions(
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("1.1.1.1"))
        {
            UseCache = true,
            // Always try the servers in the listed order (8.8.8.8 first), never at random. DnsClient
            // randomises the name server per query by default, so on a network where one resolver is
            // slow or black-holed (e.g. 1.1.1.1 blocked by a firewall) ~half the lookups would burn the
            // full timeout — making the deliverability page, which runs many sequential lookups, appear
            // to hang. Pinning the order means 1.1.1.1 is only used if 8.8.8.8 actually fails.
            UseRandomNameServer = false,
            // Generous per-query timeout: a cold/slow-but-alive 8.8.8.8 response (seen spiking to ~5s on
            // some networks) must NOT be treated as a failure, or DnsClient marks the primary down and
            // routes to the (here flaky) secondary. The deliverability checks run concurrently, so a high
            // timeout doesn't serialise page load. Retries handles one-off packet loss.
            Timeout = TimeSpan.FromSeconds(5),
            Retries = 2,
        });
    }
}

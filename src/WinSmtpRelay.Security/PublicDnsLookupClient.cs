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
            Timeout = TimeSpan.FromSeconds(5),
            Retries = 1,
        });
    }
}

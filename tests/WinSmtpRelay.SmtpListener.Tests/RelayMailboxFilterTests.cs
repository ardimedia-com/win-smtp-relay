using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using DnsClient;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;
using WinSmtpRelay.SmtpListener;

namespace WinSmtpRelay.SmtpListener.Tests;

/// <summary>
/// Direct tests of the open-relay gate <see cref="RelayMailboxFilter.CanDeliverToAsync"/>. This is the
/// security-critical decision that determines whether the relay forwards mail to an external (non-hosted)
/// domain. Only <c>context.Properties</c> and the runtime-config cache feed the decision here, so the
/// other constructor dependencies are inert stubs that the code path never invokes.
/// </summary>
[TestClass]
public class RelayMailboxFilterTests
{
    private const string ExternalDomain = "external.example.net";
    private const string HostedDomain = "hosted.example.com";
    private const string BackupMxDomain = "backup.example.org";

    private static RelayMailboxFilter CreateFilter(StubRuntimeConfigCache cache, SmtpListenerOptions? options = null)
    {
        var dns = Substitute.For<ILookupClient>();
        var spf = new SpfValidator(dns, NullLogger<SpfValidator>.Instance);
        var dmarc = new DmarcValidator(dns, new PublicSuffixService(), NullLogger<DmarcValidator>.Instance);
        var dkim = new InboundDkimVerifier(dns, NullLogger<InboundDkimVerifier>.Instance);
        var emailAuth = new EmailAuthenticationService(spf, dmarc, dkim, cache, NullLogger<EmailAuthenticationService>.Instance);
        var rateLimiter = new RateLimiter(cache, NullLogger<RateLimiter>.Instance);
        // CanDeliverToAsync never resolves a scope (only the SendAs path in CanAcceptFromAsync does),
        // so an unconfigured substitute is sufficient.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        return new RelayMailboxFilter(
            Options.Create(options ?? new SmtpListenerOptions()),
            emailAuth,
            rateLimiter,
            cache,
            scopeFactory,
            NullLogger<RelayMailboxFilter>.Instance);
    }

    /// <summary>
    /// Builds an <see cref="ISessionContext"/> whose <c>Properties</c> dictionary carries the values the
    /// filter reads (remote endpoint, authenticated user, tenant id). NSubstitute supplies a real
    /// dictionary so the production reads/writes behave normally.
    /// </summary>
    private static ISessionContext CreateContext(
        string? clientIp = null,
        string? authenticatedUser = null,
        int? tenantId = null)
    {
        var context = Substitute.For<ISessionContext>();
        var props = new Dictionary<string, object>();
        if (clientIp is not null)
            props[EndpointListener.RemoteEndPointKey] = new IPEndPoint(IPAddress.Parse(clientIp), 12345);
        if (authenticatedUser is not null)
            props["AuthenticatedUser"] = authenticatedUser;
        if (tenantId is not null)
            props["TenantId"] = tenantId.Value;
        context.Properties.Returns(props);
        return context;
    }

    private static IMailbox Recipient(string domain) => new Mailbox("rcpt", domain);
    private static readonly IMailbox Sender = new Mailbox("sender", "sender.example.com");

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Authenticated_ExternalRecipient_Allowed()
    {
        // An authenticated session may relay to any external domain — the open-relay gate is satisfied
        // by authentication regardless of IP rules.
        var cache = new StubRuntimeConfigCache();
        var filter = CreateFilter(cache);
        var context = CreateContext(clientIp: "203.0.113.50", authenticatedUser: "alice", tenantId: TenantDefaults.DefaultTenantId);

        var allowed = await filter.CanDeliverToAsync(context, Recipient(ExternalDomain), Sender, CancellationToken.None);

        Assert.IsTrue(allowed);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Unauthenticated_NoAllowRule_ExternalRecipient_Denied()
    {
        // The secure default: no authentication, no allow-IP rule, external recipient → relaying refused.
        var cache = new StubRuntimeConfigCache();
        var filter = CreateFilter(cache);
        var context = CreateContext(clientIp: "203.0.113.50", tenantId: TenantDefaults.DefaultTenantId);

        var allowed = await filter.CanDeliverToAsync(context, Recipient(ExternalDomain), Sender, CancellationToken.None);

        Assert.IsFalse(allowed);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Unauthenticated_ExplicitAllowRule_ExternalRecipient_Allowed()
    {
        // An explicit, non-broad Allow rule matching the client IP authorizes external relaying even
        // without authentication.
        var cache = new StubRuntimeConfigCache
        {
            IpAccessRules =
            [
                new IpAccessRule { TenantId = TenantDefaults.DefaultTenantId, Network = "10.0.0.0/8", Action = IpAccessAction.Allow, SortOrder = 0 }
            ]
        };
        var filter = CreateFilter(cache);
        var context = CreateContext(clientIp: "10.0.0.5", tenantId: TenantDefaults.DefaultTenantId);

        var allowed = await filter.CanDeliverToAsync(context, Recipient(ExternalDomain), Sender, CancellationToken.None);

        Assert.IsTrue(allowed);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Unauthenticated_BroadAnyAllowRule_ExternalRecipient_Denied()
    {
        // A single 0.0.0.0/0 Allow rule must NOT turn the relay open — open-relay protection cannot be
        // disabled by an "any" rule.
        var cache = new StubRuntimeConfigCache
        {
            IpAccessRules =
            [
                new IpAccessRule { TenantId = TenantDefaults.DefaultTenantId, Network = "0.0.0.0/0", Action = IpAccessAction.Allow, SortOrder = 0 }
            ]
        };
        var filter = CreateFilter(cache);
        var context = CreateContext(clientIp: "203.0.113.50", tenantId: TenantDefaults.DefaultTenantId);

        var allowed = await filter.CanDeliverToAsync(context, Recipient(ExternalDomain), Sender, CancellationToken.None);

        Assert.IsFalse(allowed);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task HostedRecipientDomain_Accepted_EvenUnauthenticated()
    {
        // Mail to a domain we host (accepted recipient domain) is inbound — accepted without auth and
        // without any IP allow rule.
        var cache = new StubRuntimeConfigCache { AcceptedDomains = [HostedDomain] };
        var filter = CreateFilter(cache);
        var context = CreateContext(clientIp: "203.0.113.50", tenantId: TenantDefaults.DefaultTenantId);

        var allowed = await filter.CanDeliverToAsync(context, Recipient(HostedDomain), Sender, CancellationToken.None);

        Assert.IsTrue(allowed);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task BackupMxDomain_Accepted_EvenUnauthenticated()
    {
        // A backup-MX domain is hosted — accepted without auth.
        var cache = new StubRuntimeConfigCache
        {
            BackupMxSettings = new BackupMxSettings { Enabled = true, Domains = BackupMxDomain }
        };
        var filter = CreateFilter(cache);
        var context = CreateContext(clientIp: "203.0.113.50", tenantId: TenantDefaults.DefaultTenantId);

        var allowed = await filter.CanDeliverToAsync(context, Recipient(BackupMxDomain), Sender, CancellationToken.None);

        Assert.IsTrue(allowed);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task BackupMxDisabled_NonHostedDomain_FallsThroughToRelayGate()
    {
        // When backup-MX is disabled, its domain is not hosted, so an unauthenticated client with no
        // allow rule is denied (proves the Enabled flag actually gates the backup-MX acceptance).
        var cache = new StubRuntimeConfigCache
        {
            BackupMxSettings = new BackupMxSettings { Enabled = false, Domains = BackupMxDomain }
        };
        var filter = CreateFilter(cache);
        var context = CreateContext(clientIp: "203.0.113.50", tenantId: TenantDefaults.DefaultTenantId);

        var allowed = await filter.CanDeliverToAsync(context, Recipient(BackupMxDomain), Sender, CancellationToken.None);

        Assert.IsFalse(allowed);
    }
}

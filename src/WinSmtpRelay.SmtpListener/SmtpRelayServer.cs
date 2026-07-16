using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.ComponentModel;
using SmtpServer.Net;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.SmtpListener;

public class SmtpRelayServer : BackgroundService
{
    private readonly SmtpListenerOptions _config;
    private readonly RelayMessageStore _messageStore;
    private readonly RelayMailboxFilter _mailboxFilter;
    private readonly CertificateLoader _certificateLoader;
    private readonly IUserAuthenticator _userAuthenticator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRejectionRecorder _rejectionRecorder;
    private readonly ILogger<SmtpRelayServer> _logger;

    public SmtpRelayServer(
        IOptions<SmtpListenerOptions> options,
        RelayMessageStore messageStore,
        RelayMailboxFilter mailboxFilter,
        CertificateLoader certificateLoader,
        IUserAuthenticator userAuthenticator,
        IServiceScopeFactory scopeFactory,
        IRejectionRecorder rejectionRecorder,
        ILogger<SmtpRelayServer> logger)
    {
        _config = options.Value;
        _messageStore = messageStore;
        _mailboxFilter = mailboxFilter;
        _certificateLoader = certificateLoader;
        _userAuthenticator = userAuthenticator;
        _scopeFactory = scopeFactory;
        _rejectionRecorder = rejectionRecorder;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoints = await LoadEndpointsAsync(stoppingToken);
        if (endpoints.Count == 0)
        {
            _logger.LogWarning("No SMTP endpoints configured (no enabled receive connectors and SmtpListener:Endpoints is empty) — the SMTP listener will not start.");
            return;
        }

        var certificate = _certificateLoader.LoadCertificate();
        var hasTlsEndpoints = endpoints.Any(e => e.ImplicitTls || e.RequireTls);

        if (hasTlsEndpoints && certificate == null)
        {
            _logger.LogError("TLS endpoints configured but no certificate available. " +
                             "Configure Tls:CertificatePath or Tls:CertificateThumbprint.");
            return;
        }

        var optionsBuilder = new SmtpServerOptionsBuilder()
            .ServerName("WinSmtpRelay")
            .MaxMessageSize(_config.MaxMessageSizeBytes);

        foreach (var endpoint in endpoints)
        {
            var listenAddress = IPAddress.Parse(endpoint.Address);
            optionsBuilder.Endpoint(builder =>
            {
                builder.Port(endpoint.Port, endpoint.ImplicitTls);
                builder.Endpoint(new IPEndPoint(listenAddress, endpoint.Port));

                if (endpoint.RequireAuth)
                {
                    // AuthenticationRequired makes the library refuse MAIL FROM until the session has
                    // authenticated (530). AllowUnsecureAuthentication(false) additionally forbids AUTH
                    // over a non-TLS connection. Without AuthenticationRequired a client could simply
                    // skip AUTH and still relay if it passed the IP rules.
                    builder.AuthenticationRequired();
                    builder.AllowUnsecureAuthentication(false);
                }

                if (certificate != null && (endpoint.ImplicitTls || endpoint.RequireTls))
                    builder.Certificate(certificate);
            });

            _logger.LogInformation(
                "Configured SMTP endpoint on {Address}:{Port} (ImplicitTls={ImplicitTls}, RequireTls={RequireTls}, Auth={RequireAuth})",
                endpoint.Address, endpoint.Port, endpoint.ImplicitTls, endpoint.RequireTls, endpoint.RequireAuth);
        }

        // Open-relay protection is ALWAYS enforced in RelayMailboxFilter.CanDeliverToAsync: relaying to an
        // external (non-hosted) domain requires SMTP authentication or an explicit, non-"any" allow-IP
        // rule. It cannot be disabled by configuration. Log the resulting posture for the operator.
        {
            using var scope = _scopeFactory.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<IRuntimeConfigCache>();
            var ipRules = await cache.GetIpAccessRulesAsync(stoppingToken);
            var explicitRelayNetworks =
                ipRules.Count(r => r.Action == IpAccessAction.Allow && !IpAccessEvaluator.IsAnyNetwork(r.Network)) +
                _config.AllowedNetworks.Count(n => !IpAccessEvaluator.IsAnyNetwork(n));
            var anyAllowConfigured =
                ipRules.Any(r => r.Action == IpAccessAction.Allow && IpAccessEvaluator.IsAnyNetwork(r.Network)) ||
                _config.AllowedNetworks.Any(IpAccessEvaluator.IsAnyNetwork);

            _logger.LogInformation(
                "Open-relay protection active: external relaying requires SMTP authentication or an explicit " +
                "allow-IP rule. Unauthenticated relay is permitted from {Count} explicit allow network(s); " +
                "AUTH-required endpoints: {AuthEndpoints}.",
                explicitRelayNetworks, endpoints.Count(e => e.RequireAuth));

            if (anyAllowConfigured)
                _logger.LogWarning(
                    "An \"any\" allow rule (0.0.0.0/0 or ::/0) is configured. It permits connections but does NOT " +
                    "authorize external relaying — open-relay protection refuses to relay for it. Use SMTP AUTH or " +
                    "a specific allow-IP rule to permit relaying.");
        }

        var options = optionsBuilder.Build();

        var serviceProvider = new SmtpServer.ComponentModel.ServiceProvider();
        serviceProvider.Add(_messageStore);
        serviceProvider.Add(_mailboxFilter);

        serviceProvider.Add(_userAuthenticator);

        var smtpServer = new SmtpServer.SmtpServer(options, serviceProvider);

        smtpServer.SessionCreated += (sender, args) =>
        {
            _logger.LogDebug("SMTP session created from {RemoteEndPoint}",
                args.Context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep) ? ep : "unknown");

            // Protocol-level rejections. This event fires ONLY for error responses the library raises as
            // an SmtpResponseException — parser failures, out-of-sequence commands, timeouts. It does NOT
            // fire for our own policy gates: MailCommand/RcptCommand write SmtpResponse.MailboxUnavailable
            // straight to the pipe when IMailboxFilter returns false, so no exception is ever thrown and
            // this handler never sees them. Those are recorded by RelayMailboxFilter itself. Verified
            // against SmtpServer v11.1.0; re-verify on a version change (the reference floats on 11.*).
            // See design/observable-rejections.md for the full coverage map.
            args.Context.ResponseException += OnResponseException;
        };

        smtpServer.SessionCompleted += (sender, args) =>
        {
            _logger.LogDebug("SMTP session completed");
        };

        _logger.LogInformation("SMTP listener starting on {EndpointCount} endpoint(s)", endpoints.Count);

        try
        {
            await smtpServer.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("SMTP listener shutting down");
        }
    }

    /// <summary>
    /// The key SmtpServer stores the failing command's raw bytes under. It is a PRIVATE const inside
    /// SmtpServer.SmtpSession, so this is an undocumented internal we are deliberately consuming — the
    /// raw line is what makes a syntax reject diagnosable rather than merely countable. The reference
    /// floats on 11.*, so it is read best-effort: if a future version renames or drops it, the record
    /// simply loses its buffer instead of the handler breaking.
    /// </summary>
    private const string SmtpSessionBufferKey = "SmtpSession:Buffer";

    /// <summary>
    /// Records an error response the SMTP library raised. See the coverage note at the subscription:
    /// this is the protocol half only — the policy gates never reach here.
    /// </summary>
    private void OnResponseException(object? sender, SmtpResponseExceptionEventArgs e)
    {
        try
        {
            var replyCode = (int)e.Exception.Response.ReplyCode;

            // 421 is session lifecycle, not a refused submission: an idle-command timeout, a cancelled
            // session, or service shutdown. Recording it would make every TCP/banner health probe and
            // every port scanner that connects and leaves a permanent row — and a probe from inside the
            // LAN would be a *trusted* row, i.e. a standing false-positive finding. The one thing that
            // trains an operator to ignore this feature is a finding that is always there.
            if (replyCode == 421)
                return;

            var clientIp = (e.Context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep)
                ? ep as IPEndPoint
                : null)?.Address.ToString();

            byte[]? rawBuffer = null;
            if (e.Exception.Properties.TryGetValue(SmtpSessionBufferKey, out var raw) && raw is byte[] bytes)
                rawBuffer = bytes;

            var tenantId = e.Context.Properties.TryGetValue("TenantId", out var tid) && tid is int t ? t : (int?)null;

            _rejectionRecorder.Record(
                clientIp,
                Classify(replyCode, rawBuffer is not null),
                replyCode,
                tenantId: tenantId,
                detail: e.Exception.Response.Message,
                rawBuffer: RejectionBuffer.Redact(rawBuffer));
        }
        catch (Exception ex)
        {
            // Observability must never break a session, and this handler runs inside one.
            _logger.LogDebug(ex, "Recording a protocol-level rejection failed");
        }
    }

    /// <summary>
    /// Maps a library error response onto a reason. The presence of the raw buffer is the reliable
    /// discriminator: only the parser's TryMake failure attaches it.
    /// </summary>
    private static RejectReason Classify(int replyCode, bool hasBuffer) => (replyCode, hasBuffer) switch
    {
        (_, true) => RejectReason.CommandSyntaxError,
        (553, _) => RejectReason.InvalidMailboxName,
        (552, _) => RejectReason.MessageTooLarge,
        (503, _) => RejectReason.CommandSequenceError,
        _ => RejectReason.ProtocolOther
    };

    /// <summary>
    /// Endpoints come from the enabled, host-level (default-tenant) receive connectors in the
    /// database, which are the source of truth once seeded from appsettings. Binding is host
    /// infrastructure — there is one shared listening socket, so connectors are not per-tenant,
    /// and changes take effect on the next service restart. Falls back to the appsettings
    /// endpoints if the database cannot be read or has no enabled connectors.
    /// </summary>
    private async Task<List<EndpointOptions>> LoadEndpointsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var connectors = await scope.ServiceProvider
                .GetRequiredService<IReceiveConnectorService>()
                .GetAllAsync(ct);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var endpoints = new List<EndpointOptions>();
            foreach (var c in connectors.Where(c => c.IsEnabled && c.TenantId == TenantDefaults.DefaultTenantId))
            {
                // One shared socket per Address:Port — skip duplicate bindings.
                if (!seen.Add($"{c.Address}:{c.Port}"))
                {
                    _logger.LogWarning("Skipping duplicate receive connector '{Name}' on {Address}:{Port}", c.Name, c.Address, c.Port);
                    continue;
                }

                endpoints.Add(new EndpointOptions
                {
                    Address = c.Address,
                    Port = c.Port,
                    RequireTls = c.RequireTls,
                    ImplicitTls = c.ImplicitTls,
                    RequireAuth = c.RequireAuth
                });
            }

            if (endpoints.Count > 0)
            {
                _logger.LogInformation("Loaded {Count} receive connector(s) from the database", endpoints.Count);
                return endpoints;
            }

            _logger.LogInformation("No enabled receive connectors in the database; falling back to appsettings endpoints");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load receive connectors from the database; falling back to appsettings endpoints");
        }

        return _config.Endpoints;
    }
}

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
    private readonly ILogger<SmtpRelayServer> _logger;

    public SmtpRelayServer(
        IOptions<SmtpListenerOptions> options,
        RelayMessageStore messageStore,
        RelayMailboxFilter mailboxFilter,
        CertificateLoader certificateLoader,
        IUserAuthenticator userAuthenticator,
        IServiceScopeFactory scopeFactory,
        ILogger<SmtpRelayServer> logger)
    {
        _config = options.Value;
        _messageStore = messageStore;
        _mailboxFilter = mailboxFilter;
        _certificateLoader = certificateLoader;
        _userAuthenticator = userAuthenticator;
        _scopeFactory = scopeFactory;
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
                    builder.AllowUnsecureAuthentication(false);

                if (certificate != null && (endpoint.ImplicitTls || endpoint.RequireTls))
                    builder.Certificate(certificate);
            });

            _logger.LogInformation(
                "Configured SMTP endpoint on {Address}:{Port} (ImplicitTls={ImplicitTls}, RequireTls={RequireTls}, Auth={RequireAuth})",
                endpoint.Address, endpoint.Port, endpoint.ImplicitTls, endpoint.RequireTls, endpoint.RequireAuth);
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

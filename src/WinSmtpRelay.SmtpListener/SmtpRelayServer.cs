using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.ComponentModel;
using SmtpServer.Net;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.SmtpListener;

public class SmtpRelayServer : BackgroundService
{
    private readonly SmtpListenerOptions _config;
    private readonly RelayMessageStore _messageStore;
    private readonly RelayMailboxFilter _mailboxFilter;
    private readonly CertificateLoader _certificateLoader;
    private readonly IUserAuthenticator _userAuthenticator;
    private readonly ILogger<SmtpRelayServer> _logger;

    public SmtpRelayServer(
        IOptions<SmtpListenerOptions> options,
        RelayMessageStore messageStore,
        RelayMailboxFilter mailboxFilter,
        CertificateLoader certificateLoader,
        IUserAuthenticator userAuthenticator,
        ILogger<SmtpRelayServer> logger)
    {
        _config = options.Value;
        _messageStore = messageStore;
        _mailboxFilter = mailboxFilter;
        _certificateLoader = certificateLoader;
        _userAuthenticator = userAuthenticator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var certificate = _certificateLoader.LoadCertificate();
        var hasTlsEndpoints = _config.Endpoints.Any(e => e.ImplicitTls || e.RequireTls);

        if (hasTlsEndpoints && certificate == null)
        {
            _logger.LogError("TLS endpoints configured but no certificate available. " +
                             "Configure Tls:CertificatePath or Tls:CertificateThumbprint.");
            return;
        }

        var optionsBuilder = new SmtpServerOptionsBuilder()
            .ServerName("WinSmtpRelay")
            .MaxMessageSize(_config.MaxMessageSizeBytes);

        foreach (var endpoint in _config.Endpoints)
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

        var serviceProvider = new ServiceProvider();
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

        _logger.LogInformation("SMTP listener starting on {EndpointCount} endpoint(s)", _config.Endpoints.Count);

        try
        {
            await smtpServer.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("SMTP listener shutting down");
        }
    }
}

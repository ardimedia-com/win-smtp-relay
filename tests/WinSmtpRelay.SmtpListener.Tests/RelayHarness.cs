using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DnsClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SmtpServer.Authentication;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Security;

namespace WinSmtpRelay.SmtpListener.Tests;

/// <summary>
/// The real SMTP listener (<see cref="SmtpRelayServer"/> with the real filter, store, authenticator and
/// session pipe) on a loopback socket, with the database replaced by in-memory fakes. Tests talk raw SMTP
/// to it, so what they assert is what a client on the wire receives.
/// </summary>
internal sealed class RelayHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly SmtpRelayServer _server;
    private readonly string? _pfxPath;

    private RelayHarness(ServiceProvider services, SmtpRelayServer server, int port, string? pfxPath)
    {
        _services = services;
        _server = server;
        Port = port;
        _pfxPath = pfxPath;
    }

    public int Port { get; }

    public StubRuntimeConfigCache Cache => _services.GetRequiredService<StubRuntimeConfigCache>();
    public FakeMessageQueue Queue => _services.GetRequiredService<FakeMessageQueue>();
    public RecordingRejectionRecorder Recorder => _services.GetRequiredService<RecordingRejectionRecorder>();
    public FakeUserService Users => _services.GetRequiredService<FakeUserService>();

    public static async Task<RelayHarness> StartAsync(
        StubRuntimeConfigCache? cache = null,
        Action<SmtpListenerOptions>? configure = null,
        Func<int, EndpointOptions>? endpoint = null,
        bool withCertificate = false)
    {
        var port = FreePort();
        string? pfxPath = null;

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(new TestOutputLoggerProvider()));
        services.AddOptions();
        services.Configure<SmtpListenerOptions>(o =>
        {
            o.Endpoints = [endpoint?.Invoke(port) ?? new EndpointOptions { Address = "127.0.0.1", Port = port }];
            configure?.Invoke(o);
        });

        if (withCertificate)
        {
            pfxPath = Path.Combine(Path.GetTempPath(), $"smtp_harness_{Guid.NewGuid()}.pfx");
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, "pw"));
            services.Configure<TlsOptions>(o =>
            {
                o.CertificatePath = pfxPath;
                o.CertificatePassword = "pw";
            });
        }

        var stubCache = cache ?? new StubRuntimeConfigCache();
        services.AddSingleton(stubCache);
        services.AddSingleton<IRuntimeConfigCache>(stubCache);

        // No DNS from tests: SPF/DMARC never run for a loopback client anyway.
        services.AddSingleton(Substitute.For<ILookupClient>());
        services.AddRelaySecurity();

        var connectors = Substitute.For<IReceiveConnectorService>();
        connectors.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ReceiveConnector>());
        services.AddSingleton(connectors);

        services.AddSingleton<FakeMessageQueue>();
        services.AddSingleton<IMessageQueue>(sp => sp.GetRequiredService<FakeMessageQueue>());
        services.AddSingleton<FakeUserService>();
        services.AddSingleton<IUserService>(sp => sp.GetRequiredService<FakeUserService>());
        services.AddSingleton<RecordingRejectionRecorder>();
        services.AddSingleton<IRejectionRecorder>(sp => sp.GetRequiredService<RecordingRejectionRecorder>());
        services.AddSingleton<IActivityNotifier, NullActivityNotifier>();

        services.AddSingleton<CertificateLoader>();
        services.AddSingleton<RelayMessageStore>();
        services.AddSingleton<RelayMailboxFilter>();
        services.AddSingleton<RelayUserAuthenticator>();
        services.AddSingleton<IUserAuthenticator>(sp => sp.GetRequiredService<RelayUserAuthenticator>());
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<WebhookService>();
        services.AddHttpClient("Webhook");
        services.AddSingleton<SmtpRelayServer>();

        var provider = services.BuildServiceProvider();

        // Failed AUTH answers are padded to a floor in production; tests do not need to wait for it.
        provider.GetRequiredService<RelayUserAuthenticator>().FailureResponseFloor = TimeSpan.Zero;

        var server = provider.GetRequiredService<SmtpRelayServer>();
        await server.StartAsync(CancellationToken.None);
        await WaitForListenerAsync(port, server);

        return new RelayHarness(provider, server, port, pfxPath);
    }

    public async Task<SmtpTestClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port);
        return new SmtpTestClient(client);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WaitForListenerAsync(int port, SmtpRelayServer server)
    {
        for (var i = 0; i < 100; i++)
        {
            if (server.ExecuteTask is { IsCompleted: true } done)
                throw new InvalidOperationException("The SMTP listener stopped during start-up.", done.Exception);

            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException($"The SMTP listener did not start on port {port}.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _server.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await _services.DisposeAsync();
        if (_pfxPath is not null)
            File.Delete(_pfxPath);
    }
}

/// <summary>A raw SMTP client: one command, one final reply line (multi-line replies are drained).</summary>
internal sealed class SmtpTestClient : IDisposable
{
    private readonly TcpClient _client;
    private Stream _stream;
    private StreamReader _reader;
    private StreamWriter _writer;

    public SmtpTestClient(TcpClient client)
    {
        _client = client;
        var network = client.GetStream();
        network.ReadTimeout = 15000;
        network.WriteTimeout = 15000;
        _stream = network;
        (_reader, _writer) = Wrap(_stream);
        Banner = _reader.ReadLine();
    }

    public string? Banner { get; }

    private static (StreamReader, StreamWriter) Wrap(Stream stream) =>
        (new StreamReader(stream, Encoding.Latin1, false, 1024, leaveOpen: true),
         new StreamWriter(stream, Encoding.Latin1, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" });

    public string? Send(string command)
    {
        _writer.WriteLine(command);
        return ReadReply();
    }

    /// <summary>Sends the body after a 354 (dot-stuffing is the caller's business) and returns the final reply.</summary>
    public string? SendBody(string body)
    {
        _writer.Write(body.EndsWith("\r\n", StringComparison.Ordinal) ? body : body + "\r\n");
        _writer.Write(".\r\n");
        return ReadReply();
    }

    /// <summary>Writes raw bytes without any terminator.</summary>
    public void WriteRaw(byte[] bytes)
    {
        _writer.Flush();
        _stream.Write(bytes);
        _stream.Flush();
    }

    public string? ReadReply()
    {
        string? line;
        while ((line = _reader.ReadLine()) is not null)
        {
            if (line.Length < 4 || line[3] != '-')
                break;
        }

        return line;
    }

    /// <summary>The full multi-line reply (e.g. EHLO), one entry per line.</summary>
    public List<string> SendMultiline(string command)
    {
        _writer.WriteLine(command);
        var lines = new List<string>();
        string? line;
        while ((line = _reader.ReadLine()) is not null)
        {
            lines.Add(line);
            if (line.Length < 4 || line[3] != '-')
                break;
        }

        return lines;
    }

    /// <summary>STARTTLS, then continue the session over TLS (any server certificate is accepted).</summary>
    public string? StartTls()
    {
        var reply = Send("STARTTLS");
        if (reply is null || !reply.StartsWith("220", StringComparison.Ordinal))
            return reply;

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: true);
        ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        });
        _stream = ssl;
        (_reader, _writer) = Wrap(_stream);
        return reply;
    }

    /// <summary>True when the server has closed the connection (EOF or reset).</summary>
    public bool IsClosedByServer()
    {
        try
        {
            return _reader.ReadLine() is null;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        _stream.Dispose();
        _client.Dispose();
    }
}

internal sealed class FakeMessageQueue : IMessageQueue
{
    private long _nextId;

    public ConcurrentQueue<QueuedMessage> Messages { get; } = new();

    public Task<long> EnqueueAsync(QueuedMessage message, CancellationToken cancellationToken = default)
    {
        message.Id = Interlocked.Increment(ref _nextId);
        Messages.Enqueue(message);
        return Task.FromResult(message.Id);
    }

    public Task<IReadOnlyList<QueuedMessage>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpdateStatusAsync(long messageId, MessageStatus status, string? error = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<QueuedMessage?> GetByIdAsync(long messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default) => Task.FromResult(Messages.Count);
    public Task SetRetryAsync(long messageId, int retryCount, DateTimeOffset nextRetryUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> RequeueAsync(long messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(long messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<QueuedMessage>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<QueuedMessage>> GetNonDeliveredAsync(int maxCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<int> RequeueStaleDeliveringAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetDeliveredRecipientsAsync(long messageId, string deliveredRecipients, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task StripBodyAsync(long messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>Users by (username, password); the password is compared in plain text — this is a fake.</summary>
internal sealed class FakeUserService : IUserService
{
    public List<(RelayUser User, string Password)> Users { get; } = [];

    public Task<RelayUser?> ValidateAndGetAsync(string username, string password, CancellationToken cancellationToken = default) =>
        Task.FromResult<RelayUser?>(Users.FirstOrDefault(u => u.User.Username == username && u.Password == password && u.User.IsEnabled).User);

    public Task<RelayUser?> GetByUsernameAsync(string username, int tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<RelayUser?>(Users.FirstOrDefault(u => u.User.Username == username && u.User.TenantId == tenantId).User);

    public Task<bool> ValidateCredentialsAsync(string username, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RelayUser?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CreateUserAsync(string username, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<RelayUser>> GetAllUsersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpdateUserAsync(int userId, bool isEnabled, string? allowedSenderAddresses, int? rateLimitPerMinute, int? rateLimitPerDay, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteUserAsync(int userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>Captures every recorded rejection, so a test can assert WHICH gate refused.</summary>
internal sealed class RecordingRejectionRecorder : IRejectionRecorder
{
    public ConcurrentQueue<(string? ClientIp, RejectReason Reason, int ReplyCode, string? SenderDomain, int? TenantId, string? Detail, string? RawBuffer)> Recorded { get; } = new();

    public void Record(
        string? clientIp,
        RejectReason reason,
        int replyCode,
        string? senderDomain = null,
        int? tenantId = null,
        string? detail = null,
        string? rawBuffer = null)
        => Recorded.Enqueue((clientIp, reason, replyCode, senderDomain, tenantId, detail, rawBuffer));
}

/// <summary>Writes log entries to the test output, so a failing session test shows what the server saw.</summary>
internal sealed class TestOutputLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);
    public void Dispose() { }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            Console.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? "" : " " + exception.GetType().Name + ": " + exception.Message)}");
        }
    }
}

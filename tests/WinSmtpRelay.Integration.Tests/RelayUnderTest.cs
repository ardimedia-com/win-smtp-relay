using System.Net.Sockets;
using System.Text;
using DnsClient;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.SmtpListener;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Integration.Tests;

/// <summary>A relay on a real socket with a real SQLite database, plus the raw SMTP dialogue to poke it.</summary>
internal sealed class RelayUnderTest : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _dbPath;
    private readonly int _port;

    private RelayUnderTest(IHost host, string dbPath, int port)
    {
        _host = host;
        _dbPath = dbPath;
        _port = port;
    }

    public static async Task<RelayUnderTest> StartAsync(int port, string[] allowedNetworks)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"winsmtprelay_rejects_{Guid.NewGuid()}.db");

        var builder = Host.CreateApplicationBuilder();
        builder.Services.Configure<SmtpListenerOptions>(o =>
        {
            o.Endpoints = [new EndpointOptions { Port = port }];
            o.AllowedNetworks = [.. allowedNetworks];
            o.AcceptedDomains = [];
        });
        builder.Services.AddRelayStorage($"Data Source={dbPath}");
        // Required for the model to match the migration snapshot, even though this test never signs
        // anyone in. IdentityDbContext.OnModelCreating resolves IOptions<IdentityOptions> from the
        // APPLICATION service provider and reads Stores.SchemaVersion to decide whether the model has
        // the v3 passkey table. The snapshot was generated against the Service host, which sets v3
        // (see RelayAuthExtensions.AddRelayAdminAuth), so a host that leaves it at the default builds
        // a different model and MigrateAsync fails with PendingModelChangesWarning. Registering the
        // option alone is enough — the full auth stack needs ASP.NET routing, which a generic host
        // does not have.
        builder.Services.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        builder.Services.AddSingleton<ILookupClient>(new LookupClient());
        builder.Services.Configure<EmailAuthenticationOptions>(_ => { });
        builder.Services.AddSmtpListener();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var host = builder.Build();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Database.MigrateAsync();

        await host.StartAsync();
        await Task.Delay(500); // let the listener bind

        return new RelayUnderTest(host, dbPath, port);
    }

    public async Task TalkAsync(Action<RawSmtpSession> dialogue)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

        await reader.ReadLineAsync(); // banner
        dialogue(new RawSmtpSession(reader, writer));
    }

    /// <summary>
    /// Stops the host, which triggers the recorder's final flush — so the assertions do not have to
    /// sleep out the five-second interval, and the graceful-stop flush gets exercised as a side effect.
    /// </summary>
    public async Task<IReadOnlyList<RejectedSubmission>> StopAndReadRowsAsync()
    {
        await _host.StopAsync();
        return await QueryAsync(db => db.RejectedSubmissions.AsNoTracking().ToListAsync());
    }

    /// <summary>Runs a query against the relay's own database, on its own scope.</summary>
    public async Task<T> QueryAsync<T>(Func<RelayDbContext, Task<T>> query)
    {
        using var scope = _host.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<RelayDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        await Task.Delay(50); // let SQLite release the file
        try { File.Delete(_dbPath); } catch { /* a leaked temp file must not fail a test */ }
    }
}

internal sealed class RawSmtpSession(StreamReader reader, StreamWriter writer)
{
    /// <summary>
    /// Writes one command, drains its reply (including a multi-line EHLO response), and returns the
    /// final reply line — which starts with the SMTP code the client actually received.
    /// </summary>
    public string? Send(string command)
    {
        writer.WriteLine(command);
        return ReadReply();
    }

    /// <summary>
    /// Writes several commands in ONE write (RFC 2920 pipelining, so they arrive in one buffer) and
    /// returns one final reply line per command, in order.
    /// </summary>
    public IReadOnlyList<string?> SendPipelined(params string[] commands)
    {
        writer.Write(string.Concat(commands.Select(c => c + "\r\n")));
        return commands.Select(_ => ReadReply()).ToList();
    }

    /// <summary>Writes a message body after a 354 and its end-of-data marker; returns the final reply.</summary>
    public string? SendBody(string body)
    {
        writer.Write(body + "\r\n.\r\n");
        return ReadReply();
    }

    private string? ReadReply()
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // A continuation line is "250-text"; the final one is "250 text".
            if (line.Length < 4 || line[3] != '-')
                break;
        }

        return line;
    }
}

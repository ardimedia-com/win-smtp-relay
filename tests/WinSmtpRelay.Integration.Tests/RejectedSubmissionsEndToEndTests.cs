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

/// <summary>
/// Proves the two-source architecture at runtime against a real listener and a real database.
/// <para>
/// This exists because the feature's design turns on a claim about the SMTP library's internals — that
/// its <c>ResponseException</c> event fires for parser failures but NOT for the relay's own policy gates,
/// which write their reply straight to the pipe. That claim was established by reading the library
/// source; these tests are what keep it true. The reference floats on <c>11.*</c>, so if a future version
/// changes which exits raise the event, the coverage silently shifts and one of these tests is the thing
/// that says so.
/// </para>
/// </summary>
[TestClass]
public class RejectedSubmissionsEndToEndTests
{
    private const int SyntaxTestPort = 9027;
    private const int PolicyTestPort = 9028;
    private const int FilterTestPort = 9029;

    /// <summary>
    /// The protocol source: a command the parser cannot read. This class of failure previously produced no
    /// log line at all — not even a warning — so a device sending a syntactically invalid envelope was
    /// invisible even to an operator who did read the Event Log.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task MalformedCommand_IsRecordedWithTheFailingLine()
    {
        await using var relay = await RelayUnderTest.StartAsync(SyntaxTestPort, allowedNetworks: ["127.0.0.1/32"]);

        await relay.TalkAsync(session =>
        {
            session.Send("EHLO tester");
            session.Send("MAIL FROM:<broken@@@example.com");
        });

        var rows = await relay.StopAndReadRowsAsync();

        var syntax = rows.SingleOrDefault(r => r.Reason == RejectReason.CommandSyntaxError);
        Assert.IsNotNull(syntax, $"expected a CommandSyntaxError row; got: {Describe(rows)}");
        Assert.AreEqual("127.0.0.1", syntax.ClientIp);
        Assert.IsTrue(syntax.Count >= 1);
        Assert.IsNotNull(syntax.LastBuffer, "the raw failing line is what makes this diagnosable rather than merely countable");
        StringAssert.Contains(syntax.LastBuffer, "broken", "the record must carry the literal line the device sent");
    }

    /// <summary>
    /// The policy source. If the design's premise were wrong and the library DID raise its event for a
    /// mailbox-filter rejection, this row would carry the hook's classification instead of the gate's own
    /// reason — so this asserts the reason, not merely that something was recorded.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task PolicyRejection_IsRecordedWithTheGatesOwnReason()
    {
        // 127.0.0.1 is deliberately outside the allow-list, so MAIL FROM trips the allowed-networks gate.
        await using var relay = await RelayUnderTest.StartAsync(PolicyTestPort, allowedNetworks: ["10.99.99.0/24"]);

        await relay.TalkAsync(session =>
        {
            session.Send("EHLO tester");
            session.Send("MAIL FROM:<device@example.com>");
        });

        var rows = await relay.StopAndReadRowsAsync();

        // Exactly one row is the load-bearing assertion, not a tidiness check: it is what proves the
        // library raised no event for this rejection. If ResponseException DID fire for a mailbox-filter
        // rejection, the hook would have recorded a second, protocol-classified row alongside the gate's
        // own — and the premise the whole two-source design rests on would be wrong.
        Assert.AreEqual(1, rows.Count, $"a policy rejection must produce exactly one record; got: {Describe(rows)}");

        var policy = rows.SingleOrDefault(r => r.Reason == RejectReason.NotInAllowedNetworks);
        Assert.IsNotNull(policy, $"expected a NotInAllowedNetworks row; got: {Describe(rows)}");
        Assert.AreEqual("127.0.0.1", policy.ClientIp);
        Assert.AreEqual(550, policy.ReplyCode, "every mailbox-filter gate answers the library's fixed 550");
        Assert.AreEqual("example.com", policy.SenderDomain, "the sender domain is what a one-click 'accept this domain' acts on");
        Assert.IsFalse(policy.IsTrustedSource, "the client is outside every configured network");
        Assert.IsNull(policy.LastBuffer, "only a parser failure carries a buffer");
    }

    /// <summary>
    /// Runs the exact predicate the self-check and the Rejections page filter on. It is here rather than in
    /// a unit test because the risk is EF translation, not logic: <c>IgnoredUtc</c> is a NULLABLE
    /// DateTimeOffset carrying the SQLite ISO-string converter, and RelayDbContext's own convention note
    /// warns that comparisons on exactly that shape can fail to translate. A query that cannot translate
    /// throws only when it executes — so this executes it against real SQLite.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task IgnoringARejection_RemovesItFromTheReportedSetWithoutDeletingIt()
    {
        await using var relay = await RelayUnderTest.StartAsync(FilterTestPort, allowedNetworks: ["127.0.0.1/32"]);

        await relay.TalkAsync(session =>
        {
            session.Send("EHLO tester");
            session.Send("MAIL FROM:<broken@@@example.com");
        });
        await relay.StopAndReadRowsAsync(); // stopping flushes the aggregate

        var since = DateTimeOffset.UtcNow.AddHours(-24);

        var reported = await relay.QueryAsync(db => db.RejectedSubmissions.AsNoTracking()
            .Where(r => r.IsTrustedSource && r.IgnoredUtc == null && r.LastSeenUtc >= since)
            .ToListAsync());
        Assert.AreEqual(1, reported.Count, "a rejection from a configured network is reported");
        Assert.IsTrue(reported[0].IsTrustedSource, "127.0.0.1 is inside the configured allow-list");

        await relay.QueryAsync(async db =>
        {
            var row = await db.RejectedSubmissions.FirstAsync();
            row.IgnoredUtc = DateTimeOffset.UtcNow;
            return await db.SaveChangesAsync();
        });

        var afterIgnore = await relay.QueryAsync(db => db.RejectedSubmissions.AsNoTracking()
            .Where(r => r.IsTrustedSource && r.IgnoredUtc == null && r.LastSeenUtc >= since)
            .ToListAsync());
        Assert.AreEqual(0, afterIgnore.Count, "an ignored rejection stops being reported");

        var all = await relay.QueryAsync(db => db.RejectedSubmissions.AsNoTracking().ToListAsync());
        Assert.AreEqual(1, all.Count, "ignoring must not delete the row — it keeps counting, it just stops nagging");
    }

    private static string Describe(IReadOnlyList<RejectedSubmission> rows) =>
        rows.Count == 0 ? "(no rows)" : string.Join(", ", rows.Select(r => $"{r.Reason}x{r.Count}"));

    /// <summary>A relay on a real socket with a real SQLite database, plus the raw SMTP dialogue to poke it.</summary>
    private sealed class RelayUnderTest : IAsyncDisposable
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

        public async Task TalkAsync(Action<SmtpSession> dialogue)
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", _port);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            await reader.ReadLineAsync(); // banner
            dialogue(new SmtpSession(reader, writer));
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

    private sealed class SmtpSession(StreamReader reader, StreamWriter writer)
    {
        /// <summary>Writes one command and drains its reply (including a multi-line EHLO response).</summary>
        public void Send(string command)
        {
            writer.WriteLine(command);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                // A continuation line is "250-text"; the final one is "250 text".
                if (line.Length < 4 || line[3] != '-')
                    break;
            }
        }
    }
}

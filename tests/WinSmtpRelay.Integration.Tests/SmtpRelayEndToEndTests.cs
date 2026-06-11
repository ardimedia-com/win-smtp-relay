using DnsClient;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Delivery;
using WinSmtpRelay.SmtpListener;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Integration.Tests;

[TestClass]
public class SmtpRelayEndToEndTests
{
    private const int TestSmtpPort = 9025;

    /// <summary>
    /// Starts the relay on port 9025, sends a message via SMTP client,
    /// verifies the message was queued in SQLite.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task SendEmail_QueuesMessageInDatabase()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"winsmtprelay_test_{Guid.NewGuid()}.db");

        try
        {
            var builder = Host.CreateApplicationBuilder();

            builder.Services.Configure<SmtpListenerOptions>(o =>
            {
                o.Endpoints = [new EndpointOptions { Port = TestSmtpPort }];
                o.AllowedNetworks = ["127.0.0.1/32", "::1/128"];
                o.AcceptedDomains = []; // accept all
            });

            builder.Services.Configure<DeliveryOptions>(o =>
            {
                o.MaxConcurrentDeliveries = 1;
            });

            builder.Services.AddRelayStorage($"Data Source={dbPath}");
            builder.Services.AddSingleton<ILookupClient>(new LookupClient());
            builder.Services.Configure<EmailAuthenticationOptions>(_ => { });
            builder.Services.AddSmtpListener();
            // Don't add DeliveryWorker — we only want to test queuing
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.Logging.AddConsole();

            var host = builder.Build();

            // Apply migrations
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
                await db.Database.MigrateAsync();
            }

            // Start the host (SMTP listener)
            await host.StartAsync();

            try
            {
                // Give the listener a moment to bind
                await Task.Delay(500);

                // Send a test email via SMTP
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("Test Sender", "test@winsmtprelay.local"));
                message.To.Add(new MailboxAddress("Harry", "harry@ardimedia.com"));
                message.Subject = $"WinSmtpRelay Integration Test {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
                message.Body = new TextPart("plain")
                {
                    Text = "This is a test message sent through WinSmtpRelay during an integration test."
                };

                using var client = new SmtpClient();
                await client.ConnectAsync("127.0.0.1", TestSmtpPort, MailKit.Security.SecureSocketOptions.None);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                // Verify the message was queued
                using var scope = host.Services.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();

                var depth = await queue.GetQueueDepthAsync();
                Assert.AreEqual(1, depth, "Expected exactly 1 message in queue");

                var pending = await queue.GetPendingAsync(10);
                Assert.AreEqual(1, pending.Count);
                Assert.AreEqual("test@winsmtprelay.local", pending[0].Sender);
                Assert.IsTrue(pending[0].Recipients.Contains("harry@ardimedia.com"));
                Assert.AreEqual(MessageStatus.Queued, pending[0].Status);
                Assert.IsTrue(pending[0].SizeBytes > 0);

                Console.WriteLine($"Message queued: id={pending[0].Id}, size={pending[0].SizeBytes} bytes, sourceIp={pending[0].SourceIp ?? "(not captured)"}");
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
                // Release SQLite connection pool so the file can be deleted
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    /// <summary>
    /// Full end-to-end: starts relay, sends message, delivery worker picks it up
    /// and delivers via MX to harry@ardimedia.com.
    /// Requires outbound port 25 access.
    /// </summary>
    [TestMethod]
    [TestCategory("IntegrationAuthUpdate")]
    public async Task SendEmail_DeliversViaRelay()
    {
        if (Environment.GetEnvironmentVariable("CI") is not null)
            Assert.Inconclusive("Skipping real delivery test in CI — cloud runner IPs are typically blocklisted by Spamhaus");

        if (!await IsPort25ReachableAsync())
            Assert.Inconclusive("Outbound port 25 is not reachable — skipping real delivery test");

        var dbPath = Path.Combine(Path.GetTempPath(), $"winsmtprelay_test_{Guid.NewGuid()}.db");

        try
        {
            var builder = Host.CreateApplicationBuilder();

            builder.Services.Configure<SmtpListenerOptions>(o =>
            {
                o.Endpoints = [new EndpointOptions { Port = TestSmtpPort }];
                o.AllowedNetworks = ["127.0.0.1/32", "::1/128"];
                o.AcceptedDomains = [];
            });

            builder.Services.Configure<DeliveryOptions>(o =>
            {
                o.MaxConcurrentDeliveries = 1;
                o.OpportunisticTls = true;
            });

            builder.Services.AddRelayStorage($"Data Source={dbPath}");
            builder.Services.AddSmtpListener();
            builder.Services.AddDeliveryEngine(); // Include delivery worker
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.Logging.AddConsole();

            var host = builder.Build();

            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
                await db.Database.MigrateAsync();
            }

            await host.StartAsync();

            try
            {
                await Task.Delay(500);

                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("WinSmtpRelay Test", "relay-test@ardimedia.com"));
                message.To.Add(new MailboxAddress("Harry", "harry@ardimedia.com"));
                message.Subject = $"WinSmtpRelay E2E Test {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
                // multipart/alternative (text + branded HTML, same layout as the system emails) — this
                // also verifies that the relay passes multipart MIME through the pipeline unchanged.
                var bodyContent = new WinSmtpRelay.Core.Mail.SystemEmailContent
                {
                    Title = "End-to-end test",
                    Paragraphs =
                    [
                        "This email was relayed by WinSmtpRelay during an end-to-end integration test.",
                        "If you received this, the SMTP listener, queue, and delivery engine are all working.",
                    ],
                };
                var bodyBuilder = new BodyBuilder
                {
                    TextBody = "This email was relayed by WinSmtpRelay during an end-to-end integration test.\n\n" +
                               "If you received this, the SMTP listener, queue, and delivery engine are all working.",
                    HtmlBody = WinSmtpRelay.Core.Mail.SystemEmailHtml.Render(bodyContent),
                };
                message.Body = bodyBuilder.ToMessageBody();

                using var client = new SmtpClient();
                await client.ConnectAsync("127.0.0.1", TestSmtpPort, MailKit.Security.SecureSocketOptions.None);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                Console.WriteLine("Message sent to relay, waiting for delivery...");

                // Wait for the delivery worker to deliver (up to 60 seconds)
                MessageStatus? finalStatus = null;
                string? lastError = null;
                for (var i = 0; i < 60; i++)
                {
                    await Task.Delay(1000);

                    using var checkScope = host.Services.CreateScope();
                    var db = checkScope.ServiceProvider.GetRequiredService<RelayDbContext>();
                    var msg = await db.QueuedMessages.AsNoTracking().FirstOrDefaultAsync(cancellationToken: default);

                    if (msg == null)
                    {
                        Console.WriteLine($"  Waiting... message not found in DB");
                        continue;
                    }

                    finalStatus = msg.Status;
                    lastError = msg.LastError;
                    Console.WriteLine($"  [{i + 1}s] Status={msg.Status}, RetryCount={msg.RetryCount}");

                    if (msg.Status == MessageStatus.Delivered)
                    {
                        Console.WriteLine($"Message delivered after ~{i + 1} seconds");
                        break;
                    }

                    if (msg.Status == MessageStatus.Bounced)
                    {
                        Assert.Fail($"Message bounced: {msg.LastError}");
                    }
                }

                Assert.AreEqual(MessageStatus.Delivered, finalStatus,
                    $"Expected Delivered but got {finalStatus}. LastError: {lastError}");
                Console.WriteLine("SUCCESS — check harry@ardimedia.com inbox!");
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    private static async Task<bool> IsPort25ReachableAsync()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync("gmail-smtp-in.l.google.com", 25, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort cleanup */ }
    }
}

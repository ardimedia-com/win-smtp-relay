using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinSmtpRelay.Delivery.Tests;

/// <summary>
/// A minimal plain-text SMTP server on loopback for delivery tests: EHLO (optionally advertising AUTH), AUTH PLAIN,
/// MAIL, RCPT (per-recipient reply via <see cref="RcptReply"/>), DATA, RSET, QUIT. No STARTTLS. Records every
/// transaction and every command line.
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public FakeSmtpServer(bool advertiseAuth = false)
    {
        AdvertiseAuth = advertiseAuth;
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool AdvertiseAuth { get; }

    /// <summary>Reply to RCPT TO for an address; default "250 OK".</summary>
    public Func<string, string> RcptReply { get; set; } = _ => "250 2.1.5 OK";

    /// <summary>Completed transactions (MAIL FROM + accepted RCPTs + data).</summary>
    public ConcurrentQueue<Transaction> Transactions { get; } = new();

    /// <summary>Every command line received, in order.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    public sealed record Transaction(string MailFrom, IReadOnlyList<string> Recipients, string Data);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        try
        {
            await writer.WriteLineAsync("220 fake.test ESMTP");
            string? mailFrom = null;
            var rcpts = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
            {
                Commands.Enqueue(line);
                var upper = line.ToUpperInvariant();
                if (upper.StartsWith("EHLO"))
                {
                    await writer.WriteLineAsync("250-fake.test");
                    if (AdvertiseAuth)
                        await writer.WriteLineAsync("250-AUTH PLAIN");
                    await writer.WriteLineAsync("250 8BITMIME");
                }
                else if (upper.StartsWith("HELO"))
                    await writer.WriteLineAsync("250 fake.test");
                else if (upper.StartsWith("AUTH"))
                    await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                else if (upper.StartsWith("MAIL FROM:"))
                {
                    mailFrom = ExtractPath(line["MAIL FROM:".Length..]);
                    rcpts.Clear();
                    await writer.WriteLineAsync("250 2.1.0 OK");
                }
                else if (upper.StartsWith("RCPT TO:"))
                {
                    var rcpt = ExtractPath(line["RCPT TO:".Length..]);
                    var reply = RcptReply(rcpt);
                    if (reply.StartsWith('2'))
                        rcpts.Add(rcpt);
                    await writer.WriteLineAsync(reply);
                }
                else if (upper == "DATA")
                {
                    await writer.WriteLineAsync("354 Go ahead");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                        data.Append(dataLine).Append("\r\n");
                    Transactions.Enqueue(new Transaction(mailFrom ?? "", [.. rcpts], data.ToString()));
                    await writer.WriteLineAsync("250 2.0.0 Queued");
                }
                else if (upper == "RSET")
                {
                    mailFrom = null;
                    rcpts.Clear();
                    await writer.WriteLineAsync("250 OK");
                }
                else if (upper == "QUIT")
                {
                    await writer.WriteLineAsync("221 Bye");
                    return;
                }
                else if (upper == "NOOP")
                    await writer.WriteLineAsync("250 OK");
                else
                    await writer.WriteLineAsync("502 5.5.2 Not implemented");
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static string ExtractPath(string rest)
    {
        var start = rest.IndexOf('<');
        var end = rest.IndexOf('>');
        return start >= 0 && end > start ? rest[(start + 1)..end] : rest.Trim();
    }

    /// <summary>A loopback port with nothing listening (connection refused).</summary>
    public static int UnusedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}

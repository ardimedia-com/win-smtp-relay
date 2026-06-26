using System.Diagnostics.Eventing.Reader;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// Operational/runtime health: pending EF migrations (schema drift), errors written to the Windows Event
/// Log in the last 24h, and clock skew versus an NTP reference (DKIM signatures and TLS are time-sensitive).
/// </summary>
public sealed class RuntimeHealthCheck(
    RelayDbContext db,
    IOptions<HealthCheckOptions> options,
    ILogger<RuntimeHealthCheck> logger) : HealthCheckBase
{
    public override string Name => "Runtime";
    protected override string Category => HealthCategories.Runtime;

    public override async Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct)
    {
        var f = new List<HealthFinding>();
        var opts = options.Value;

        // --- Pending EF migrations (the DB schema is behind the code) ---
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
                f.Add(Warn("pending-migrations", $"{pending.Count} pending database migration(s)",
                    $"The database schema is behind the application: {string.Join(", ", pending)}. Migrations normally apply at startup — if they persist, the DB may have been swapped under the running service.",
                    hint: "Restart the service so startup migration runs, or apply the migrations manually."));
            else
                f.Add(Ok("pending-migrations", "Database schema is up to date", "No pending EF Core migrations."));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "checking pending migrations failed");
        }

        // --- Windows Event Log errors in the last 24h ---
        f.AddRange(CheckEventLogErrors());

        // --- Clock skew vs NTP ---
        if (!string.IsNullOrWhiteSpace(opts.NtpHost))
            f.Add(await CheckClockSkewAsync(opts, ct));

        return f;
    }

    private IEnumerable<HealthFinding> CheckEventLogErrors()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        int errorCount = 0;
        string? sample = null;
        Exception? failure = null;
        try
        {
            // Application-log error/critical entries from our own provider in the last 24h (86,400,000 ms).
            const string xpath = "*[System[Provider[@Name='WinSmtpRelay'] and (Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 86400000]]]";
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, xpath));
            EventRecord? record;
            while (errorCount < 500 && (record = reader.ReadEvent()) is not null)
            {
                using (record)
                {
                    errorCount++;
                    sample ??= record.FormatDescription();
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            logger.LogWarning(failure, "reading the Windows Event Log failed");
            yield break;
        }

        if (errorCount == 0)
            yield return Ok("event-log-errors", "No service errors in the last 24h", "No error-level entries from WIN-SMTP-RELAY in the Windows Event Log.");
        else
            yield return Warn("event-log-errors", $"{errorCount} service error(s) in the last 24h",
                $"WIN-SMTP-RELAY wrote {errorCount} error-level Windows Event Log entr{(errorCount == 1 ? "y" : "ies")} in the last 24 hours."
                    + (string.IsNullOrWhiteSpace(sample) ? "" : $" Most recent: {Truncate(sample!.Trim(), 200)}"),
                hint: "Open the Event Log page for details.");
    }

    private async Task<HealthFinding> CheckClockSkewAsync(HealthCheckOptions opts, CancellationToken ct)
    {
        try
        {
            var offset = await QueryNtpOffsetAsync(opts.NtpHost, TimeSpan.FromSeconds(Math.Max(3, opts.ProbeTimeoutSeconds)), ct);
            if (offset is not { } o)
                return Info("clock-skew", "Could not check clock skew", $"No response from the NTP server {opts.NtpHost}. Best-effort only.");

            var secs = Math.Abs(o.TotalSeconds);
            if (secs >= opts.ClockSkewWarningSeconds)
                return Warn("clock-skew", $"System clock is off by {secs:F0}s",
                    $"This server's clock differs from {opts.NtpHost} by about {o.TotalSeconds:F0}s. DKIM signing and TLS are time-sensitive — a large skew can cause signature/handshake failures.",
                    hint: "Enable/repair Windows time sync (w32time) so the clock stays accurate.");
            return Ok("clock-skew", "System clock is accurate", $"Clock is within {secs:F0}s of {opts.NtpHost}.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NTP clock-skew check failed");
            return Info("clock-skew", "Could not check clock skew", $"The NTP query failed: {ex.Message}.");
        }
    }

    /// <summary>Minimal SNTP client: returns (server time − local UTC), or null on no response.</summary>
    private static async Task<TimeSpan?> QueryNtpOffsetAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var request = new byte[48];
        request[0] = 0x1B; // LI = 0, VN = 3, Mode = 3 (client)

        using var udp = new UdpClient();
        await udp.Client.ConnectAsync(host, 123, cts.Token);
        await udp.Client.SendAsync(request, SocketFlags.None, cts.Token);

        var response = new byte[48];
        var received = await udp.Client.ReceiveAsync(response, SocketFlags.None, cts.Token);
        // Local UTC is captured right after the response arrives, to compare against the server's transmit time.
        var localUtc = DateTime.UtcNow;
        if (received < 48)
            return null;

        // Transmit timestamp: seconds since 1900-01-01 at bytes 40..43, fraction at 44..47 (big-endian).
        uint seconds = (uint)((response[40] << 24) | (response[41] << 16) | (response[42] << 8) | response[43]);
        uint fraction = (uint)((response[44] << 24) | (response[45] << 16) | (response[46] << 8) | response[47]);
        var ms = seconds * 1000d + fraction * 1000d / 0x100000000L;
        var serverUtc = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
        return serverUtc - localUtc;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}

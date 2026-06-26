using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// Outbound connectivity: whether this server can deliver mail at all. Probes outbound TCP 25 (the silent
/// killer for direct-MX delivery — many ISPs/clouds block it) and the reachability + credentials of every
/// configured smart host (global and per-tenant send connectors).
/// </summary>
public sealed class ConnectivityHealthCheck(
    IMxResolver mx,
    ISendConnectorService connectors,
    IOptions<DeliveryOptions> deliveryOptions,
    IOptions<HealthCheckOptions> options,
    ILogger<ConnectivityHealthCheck> logger) : HealthCheckBase
{
    public override string Name => "Connectivity";
    protected override string Category => HealthCategories.Connectivity;

    public override async Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct)
    {
        var f = new List<HealthFinding>();
        var opts = options.Value;
        var delivery = deliveryOptions.Value;
        var timeout = TimeSpan.FromSeconds(Math.Max(5, opts.ProbeTimeoutSeconds));
        var hasGlobalSmartHost = !string.IsNullOrWhiteSpace(delivery.SmartHost);

        // --- Outbound port 25 (direct-MX delivery path) ---
        var testDomain = string.IsNullOrWhiteSpace(opts.Outbound25TestDomain) ? "gmail.com" : opts.Outbound25TestDomain.Trim();
        try
        {
            var mxHost = (await mx.ResolveMxAsync(testDomain, ct)).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(mxHost))
                f.Add(Info("outbound-port-25", "Could not test outbound port 25", $"No MX host resolved for {testDomain}, so the port-25 reachability test was skipped."));
            else if (await NetworkProbe.CanConnectAsync(mxHost, 25, timeout, ct))
                f.Add(Ok("outbound-port-25", "Outbound port 25 is open",
                    $"Connected to {mxHost}:25 — this server can deliver directly to recipient mail servers.", mxHost));
            else if (hasGlobalSmartHost)
                f.Add(Warn("outbound-port-25", "Outbound port 25 appears blocked",
                    $"Could not connect to {mxHost}:25. You relay via a smart host, so direct delivery may be unaffected — but any direct-MX route would fail.",
                    mxHost, "Confirm direct-MX delivery isn't needed, or ask the network/ISP to open outbound TCP 25."));
            else
                f.Add(Err("outbound-port-25", "Outbound port 25 is blocked",
                    $"Could not connect to {mxHost}:25 within {timeout.TotalSeconds:F0}s. Direct-MX delivery cannot work — many ISPs and cloud providers block outbound port 25.",
                    mxHost, "Open outbound TCP 25, or configure a smart host (Send Connector) to relay outbound mail."));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "outbound port 25 probe failed");
            f.Add(Info("outbound-port-25", "Could not test outbound port 25", $"The port-25 reachability test failed: {ex.Message}"));
        }

        // --- Global smart host (appsettings Delivery:SmartHost) ---
        if (hasGlobalSmartHost)
            f.Add(await ProbeSmartHostAsync("smarthost-global", "Global smart host",
                delivery.SmartHost!, delivery.SmartHostPort, delivery.OpportunisticTls, delivery.RequireTls,
                delivery.SmartHostUsername, delivery.SmartHostPassword, timeout, ct));

        // --- Per-tenant send connectors with a smart host ---
        IReadOnlyList<SendConnector> all;
        try { all = await connectors.GetAllAsync(ct); }
        catch (Exception ex) { logger.LogWarning(ex, "loading send connectors failed"); all = []; }

        foreach (var c in all.Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.SmartHost)))
            f.Add(await ProbeSmartHostAsync($"smarthost-connector:{c.Id}", $"Send connector '{c.Name}'",
                c.SmartHost!, c.SmartHostPort, c.OpportunisticTls, c.RequireTls, c.Username, c.EncryptedPassword, timeout, ct));

        return f;
    }

    // Connects to the smart host (and authenticates if credentials are set) without sending anything, to
    // prove the route works and the credentials are still valid.
    private async Task<HealthFinding> ProbeSmartHostAsync(string code, string label, string host, int port,
        bool opportunisticTls, bool requireTls, string? username, string? password, TimeSpan timeout, CancellationToken ct)
    {
        var target = $"{host}:{port}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var client = new SmtpClient { Timeout = (int)timeout.TotalMilliseconds };
        try
        {
            var secure = requireTls ? SecureSocketOptions.StartTls
                : opportunisticTls ? SecureSocketOptions.StartTlsWhenAvailable
                : SecureSocketOptions.Auto;
            await client.ConnectAsync(host, port, secure, cts.Token);

            if (!string.IsNullOrWhiteSpace(username))
            {
                try
                {
                    await client.AuthenticateAsync(username, password ?? "", cts.Token);
                }
                catch (AuthenticationException)
                {
                    await SafeDisconnectAsync(client, cts.Token);
                    return Err(code, $"{label}: authentication rejected",
                        $"Connected to {target} but the username/password was rejected. Outbound mail through this smart host will fail.",
                        target, "Update the smart host credentials.");
                }
            }

            await SafeDisconnectAsync(client, cts.Token);
            return Ok(code, $"{label} is reachable",
                $"Connected to {target}{(string.IsNullOrWhiteSpace(username) ? "" : " and authenticated successfully")}.", target);
        }
        catch (Exception ex)
        {
            return Err(code, $"{label} is unreachable",
                $"Could not connect to {target}: {ex.Message}. Outbound mail through this smart host will fail.",
                target, "Check the host/port and TLS settings, and that the smart host is up.");
        }
    }

    private static async Task SafeDisconnectAsync(SmtpClient client, CancellationToken ct)
    {
        try { if (client.IsConnected) await client.DisconnectAsync(true, ct); }
        catch { /* best-effort */ }
    }
}

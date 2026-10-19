using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;
using WinSmtpRelay.Delivery;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// Outbound connectivity: whether this server can deliver mail at all. Probes outbound TCP 25 (the silent
/// killer for direct-MX delivery — many ISPs/clouds block it) and the reachability + credentials of every
/// configured smart host (global and per-tenant send connectors).
/// </summary>
public sealed class ConnectivityHealthCheck(
    IMxResolver mx,
    ISendConnectorService connectors,
    ITenantService tenants,
    IDomainRouteService routes,
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
        OutboundRouting? routing = null;
        try { routing = await OutboundRoutingLoader.LoadAsync(tenants, connectors, routes, delivery, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "evaluating the outbound routing failed"); }

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
            else if (routing is { SmartHostOnly: true })
                f.Add(Info("outbound-port-25", "Outbound port 25 is closed — not needed",
                    $"Could not connect to {mxHost}:25, but every message leaves through a smart host ({routing.SmartHostList}) on its "
                    + "submission port, so direct delivery to recipient mail servers is never used.", mxHost));
            else if (hasGlobalSmartHost && routing is not { DirectMxPossible: true })
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

        // --- Global smart host (appsettings Delivery:SmartHost) + per-tenant send connectors with a smart host ---
        // Probed concurrently: with retries a failing host takes minutes, and the probes share no state.
        var probes = new List<Task<HealthFinding>>();
        if (hasGlobalSmartHost)
            probes.Add(ProbeSmartHostAsync("smarthost-global", "Global smart host",
                delivery.SmartHost!, delivery.SmartHostPort, delivery.OpportunisticTls, delivery.RequireTls,
                delivery.SmartHostUsername, delivery.SmartHostPassword, timeout, ct));

        IReadOnlyList<SendConnector> all;
        try { all = await connectors.GetAllAsync(ct); }
        catch (Exception ex) { logger.LogWarning(ex, "loading send connectors failed"); all = []; }

        foreach (var c in all.Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.SmartHost)))
            probes.Add(ProbeSmartHostAsync($"smarthost-connector:{c.Id}", $"Send connector '{c.Name}'",
                c.SmartHost!, c.SmartHostPort, c.OpportunisticTls, c.RequireTls, c.Username, c.EncryptedPassword, timeout, ct));

        f.AddRange(await Task.WhenAll(probes));
        return f;
    }

    /// <summary>
    /// The smart-host probes retry with a 30–60 s pause, so this check may run for minutes when a host is down;
    /// each single attempt stays bounded by <see cref="HealthCheckOptions.ProbeTimeoutSeconds"/>.
    /// </summary>
    public override TimeSpan? MaxDuration => TimeSpan.FromMinutes(15);

    // Connects to the smart host (and authenticates if credentials are set) without sending anything, to prove
    // the route works and the credentials are still valid. Up to SmartHostProbeAttempts attempts: a single
    // transient failure (DNS, timeout, TLS while the network wakes up) is a WARNING once a retry succeeds; only
    // when every attempt fails is the host reported unreachable (ERROR), with the cause of each attempt.
    private async Task<HealthFinding> ProbeSmartHostAsync(string code, string label, string host, int port,
        bool opportunisticTls, bool requireTls, string? username, string? password, TimeSpan timeout, CancellationToken ct)
    {
        var target = $"{host}:{port}";
        var opts = options.Value;
        var result = await SmartHostProbe.RunAsync(
            attemptCt => SmartHostProbe.ConnectOnceAsync(host, port, opportunisticTls, requireTls, username, password, timeout, attemptCt),
            opts.SmartHostProbeAttempts,
            TimeSpan.FromSeconds(Math.Clamp(opts.SmartHostRetryDelaySeconds, 1, 300)),
            SmartHostProbe.Describe,
            ct);

        var authenticated = string.IsNullOrWhiteSpace(username) ? "" : " and authenticated successfully";
        switch (result.Outcome)
        {
            case SmartHostProbeOutcome.Reachable:
                return Ok(code, $"{label} is reachable", $"Connected to {target}{authenticated}.", target);

            case SmartHostProbeOutcome.ReachableAfterRetry:
                logger.LogWarning("Self-check: {Label} ({Target}) reachable after retry — {Failures}", label, target, result.DescribeFailures());
                return Warn(code, $"{label} is reachable after retry (first attempt: {result.FirstFailure})",
                    $"Connected to {target}{authenticated} on attempt {result.Attempts.Count} of {opts.SmartHostProbeAttempts}. "
                    + $"Failed before: {result.DescribeFailures()}. A transient problem — delivery retries such failures automatically.",
                    target, "If this repeats, check the network/DNS of this server (e.g. power saving or standby) and the smart host's status.");

            case SmartHostProbeOutcome.AuthenticationRejected:
                return Err(code, $"{label}: authentication rejected",
                    $"Connected to {target} but the username/password was rejected ({result.FirstFailure}). Outbound mail through this smart host will fail.",
                    target, "Update the smart host credentials.");

            default:
                logger.LogWarning("Self-check: {Label} ({Target}) unreachable — {Failures}", label, target, result.DescribeFailures());
                return Err(code, $"{label} is unreachable",
                    $"Could not connect to {target} in {result.Attempts.Count} attempt(s): {result.DescribeFailures()}. "
                    + "Outbound mail through this smart host will fail.",
                    target, "Check the host/port and TLS settings, this server's network/DNS, and that the smart host is up.");
        }
    }
}

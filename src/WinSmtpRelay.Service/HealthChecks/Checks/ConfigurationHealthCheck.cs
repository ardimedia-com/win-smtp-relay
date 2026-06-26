using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Health;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Service.HealthChecks.Checks;

/// <summary>
/// Setup-correctness checks: required sending identity is filled in, reporting can actually send, signup
/// isn't unintentionally open, and every configured SMTP listener endpoint is really accepting connections.
/// </summary>
public sealed class ConfigurationHealthCheck(
    IDnsSettingsService dnsSettings,
    IReportingSettingsService reporting,
    IPortalSettingsService portal,
    IOptions<AdminUiOptions> adminUi,
    IOptions<SmtpListenerOptions> listenerOptions,
    IOptions<HealthCheckOptions> options) : HealthCheckBase
{
    public override string Name => "Configuration";
    protected override string Category => HealthCategories.Configuration;

    public override async Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct)
    {
        var f = new List<HealthFinding>();
        var dns = await dnsSettings.GetAsync(ct);

        // --- Sending identity ---
        if (string.IsNullOrWhiteSpace(dns.PublicHostname))
            f.Add(Warn("public-hostname-missing", "Public hostname not set",
                "No public hostname is configured. It's used in SPF (a:) and as the EHLO name; without it, deliverability checks can't validate the hostname.",
                hint: "Set it under Settings → Sending identity."));
        else
            f.Add(Ok("public-hostname", "Public hostname is set", $"Public hostname: {dns.PublicHostname}"));

        if (string.IsNullOrWhiteSpace(dns.SendingIpAddresses))
            f.Add(Warn("sending-ips-missing", "Sending IPs not set",
                "No sending IP addresses are configured, so SPF coverage, reverse DNS and blocklist status can't be checked.",
                hint: "Add them under Settings → Sending identity."));
        else
            f.Add(Ok("sending-ips", "Sending IPs are set", $"Configured sending IPs: {dns.SendingIpAddresses}"));

        // --- Reporting can actually send ---
        var report = await reporting.GetAsync(ct);
        if (report.Enabled)
        {
            if (string.IsNullOrWhiteSpace(report.RecipientAddress))
                f.Add(Warn("reporting-no-recipient", "Reporting is on but has no recipient",
                    "Email reporting is enabled but no recipient address is set, so the daily digest and alerts go nowhere.",
                    hint: "Set a recipient under Settings → Reporting."));
            else
            {
                var from = FirstNonBlank(report.FromAddress, (await portal.GetAsync(ct)).SignupFromAddress, adminUi.Value.SignupFromAddress);
                if (string.IsNullOrWhiteSpace(from))
                    f.Add(Warn("reporting-no-from", "Reporting is on but has no from-address",
                        "Email reporting is enabled but no from-address is configured (Reporting or Signup), so the relay can't send the digest or alerts.",
                        hint: "Set a from-address under Settings → Reporting or Host → Signup."));
                else
                    f.Add(Ok("reporting", "Reporting is configured", $"Daily digest to {report.RecipientAddress} at {report.DailyTimeUtc} UTC."));
            }
        }
        else
        {
            f.Add(Info("reporting-off", "Email reporting is off",
                "No daily digest or alert emails are sent. Enable it under Settings → Reporting to receive this self-check by email."));
        }

        // --- Signup posture ---
        var portalSettings = await portal.GetAsync(ct);
        if (portalSettings.SelfServiceSignupEnabled || adminUi.Value.SelfServiceSignupEnabled)
            f.Add(Info("signup-open", "Self-service signup is enabled",
                "Anonymous self-service tenant signup is enabled. Confirm this is intended — on a loopback-only install it usually should be off.",
                hint: "Review under Host → Signup."));

        // --- SMTP listener endpoints are actually accepting connections ---
        var timeout = TimeSpan.FromSeconds(Math.Max(3, options.Value.ProbeTimeoutSeconds));
        foreach (var ep in listenerOptions.Value.Endpoints)
        {
            var probeHost = ep.Address switch
            {
                "0.0.0.0" or "" => "127.0.0.1",
                "::" or "[::]" => "::1",
                _ => ep.Address,
            };
            var target = $"{ep.Address}:{ep.Port}";
            if (await NetworkProbe.CanConnectAsync(probeHost, ep.Port, timeout, ct))
                f.Add(Ok("listener", $"SMTP listener on port {ep.Port} is accepting", $"A connection to {target} succeeded.", target));
            else
                f.Add(Err("listener", $"SMTP listener on port {ep.Port} is not accepting",
                    $"Could not open a connection to {target}. The listener may have failed to bind (port already in use, or a permission/binding problem).",
                    target, "Check the service started cleanly and nothing else is using the port."));
        }

        return f;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

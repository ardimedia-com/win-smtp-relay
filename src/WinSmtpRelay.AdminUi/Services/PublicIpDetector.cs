using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace WinSmtpRelay.AdminUi.Services;

/// <summary>
/// Best-effort detection of the relay's public outbound IP address(es) by asking an external service.
/// Used to propose Sending IPs during setup so the operator doesn't have to look them up. The detected
/// address is the IP the relay's outbound HTTP egresses from (normally the same NAT/public IP its SMTP
/// uses); it is a proposal, not authoritative.
/// </summary>
public sealed class PublicIpDetector(IHttpClientFactory httpFactory, ILogger<PublicIpDetector> logger)
{
    // ipify returns the caller's public IP as plain text; api6 prefers IPv6 when the host has it.
    private static readonly string[] Endpoints = ["https://api.ipify.org", "https://api6.ipify.org"];

    public async Task<IReadOnlyList<string>> DetectAsync(CancellationToken ct = default)
    {
        var found = new List<string>();
        foreach (var url in Endpoints)
        {
            try
            {
                using var client = httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var text = (await client.GetStringAsync(url, ct)).Trim();
                if (IPAddress.TryParse(text, out _) && !found.Contains(text, StringComparer.OrdinalIgnoreCase))
                    found.Add(text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Public IP detection failed for {Url}", url);
            }
        }
        return found;
    }
}

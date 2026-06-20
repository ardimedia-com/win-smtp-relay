using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Locally verifies the outbound authentication for a From address without sending anything: builds the
/// message the relay would send, DKIM-signs it, verifies the signature against the relay's own key, and
/// synthesises the DMARC outcome from the published DNS. Lives in Core (interface only) so the Blazor UI
/// can resolve it without referencing the Security implementation.
/// </summary>
public interface IOutboundAuthCheckService
{
    /// <summary>Runs the local authentication self-test for a From address in the given tenant scope.</summary>
    Task<OutboundAuthCheck> CheckAsync(int tenantId, string fromAddress, CancellationToken ct = default);
}

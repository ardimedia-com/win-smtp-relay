using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IPortalSettingsService
{
    Task<PortalSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Enables or disables anonymous self-service signup at runtime (no restart needed).</summary>
    Task SetSelfServiceSignupEnabledAsync(bool enabled, CancellationToken ct = default);
}

using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IPortalSettingsService
{
    Task<PortalSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Enables or disables anonymous self-service signup at runtime (no restart needed).</summary>
    Task SetSelfServiceSignupEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Sets the signup/reset email from-address (null/blank clears it, falling back to appsettings).</summary>
    Task SetSignupFromAddressAsync(string? fromAddress, CancellationToken ct = default);

    /// <summary>Sets the per-IP-per-hour signup attempt limit (clamped to >= 0; 0 disables the throttle).</summary>
    Task SetSignupMaxAttemptsPerIpPerHourAsync(int maxPerHour, CancellationToken ct = default);
}

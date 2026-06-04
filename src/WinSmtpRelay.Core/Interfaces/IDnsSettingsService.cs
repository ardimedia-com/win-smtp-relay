using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IDnsSettingsService
{
    Task<DnsSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Updates the DNS recommendation inputs (SPF/DMARC) shown on the DNS Setup page.</summary>
    Task UpdateAsync(DnsSettings settings, CancellationToken ct = default);
}

using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class DnsSettingsService(RelayDbContext db) : IDnsSettingsService
{
    public async Task<DnsSettings> GetAsync(CancellationToken ct = default)
        => await db.DnsSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct) ?? new DnsSettings();

    public async Task UpdateAsync(DnsSettings settings, CancellationToken ct = default)
    {
        var existing = await db.DnsSettings.FindAsync([1], ct);
        if (existing is null)
        {
            existing = new DnsSettings { Id = 1 };
            db.DnsSettings.Add(existing);
        }

        existing.PublicHostname = string.IsNullOrWhiteSpace(settings.PublicHostname) ? null : settings.PublicHostname.Trim();
        existing.SendingIpAddresses = settings.SendingIpAddresses?.Trim() ?? "";
        existing.SpfIncludes = settings.SpfIncludes?.Trim() ?? "";
        existing.SpfAllQualifier = string.IsNullOrWhiteSpace(settings.SpfAllQualifier) ? "~all" : settings.SpfAllQualifier.Trim();
        existing.DmarcReportEmail = string.IsNullOrWhiteSpace(settings.DmarcReportEmail) ? null : settings.DmarcReportEmail.Trim();
        existing.DmarcPolicy = string.IsNullOrWhiteSpace(settings.DmarcPolicy) ? "none" : settings.DmarcPolicy.Trim();
        existing.DmarcPercentage = Math.Clamp(settings.DmarcPercentage, 1, 100);
        existing.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        // No cache: consumed only by the Health page (read on demand).
    }
}

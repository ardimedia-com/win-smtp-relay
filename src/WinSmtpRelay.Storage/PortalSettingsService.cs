using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class PortalSettingsService(RelayDbContext db) : IPortalSettingsService
{
    public async Task<PortalSettings> GetAsync(CancellationToken ct = default)
        => await db.PortalSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new PortalSettings();

    public async Task SetSelfServiceSignupEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var settings = await db.PortalSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new PortalSettings { Id = 1 };
            db.PortalSettings.Add(settings);
        }

        settings.SelfServiceSignupEnabled = enabled;
        // Bumps UpdatedUtc past the seed sentinel so the configuration seeder stops applying
        // the appsettings value on restart — the database becomes authoritative once edited.
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetSignupFromAddressAsync(string? fromAddress, CancellationToken ct = default)
    {
        var settings = await db.PortalSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new PortalSettings { Id = 1 };
            db.PortalSettings.Add(settings);
        }

        settings.SignupFromAddress = string.IsNullOrWhiteSpace(fromAddress) ? null : fromAddress.Trim();
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetSignupMaxAttemptsPerIpPerHourAsync(int maxPerHour, CancellationToken ct = default)
    {
        var settings = await db.PortalSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new PortalSettings { Id = 1 };
            db.PortalSettings.Add(settings);
        }

        settings.SignupMaxAttemptsPerIpPerHour = Math.Max(0, maxPerHour);
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

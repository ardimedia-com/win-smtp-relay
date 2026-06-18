using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class ReportingSettingsService(RelayDbContext db) : IReportingSettingsService
{
    public async Task<ReportingSettings> GetAsync(CancellationToken ct = default)
        => await db.ReportingSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct) ?? new ReportingSettings();

    public async Task UpdateAsync(bool enabled, string? recipientAddress, string? fromAddress, string dailyTimeUtc,
        int bounceRateAlertPercent, CancellationToken ct = default)
    {
        var settings = await Row(ct);
        settings.Enabled = enabled;
        settings.RecipientAddress = Blank(recipientAddress);
        settings.FromAddress = Blank(fromAddress);
        settings.DailyTimeUtc = string.IsNullOrWhiteSpace(dailyTimeUtc) ? "06:00" : dailyTimeUtc.Trim();
        settings.BounceRateAlertPercent = Math.Clamp(bounceRateAlertPercent, 0, 100);
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkDigestSentAsync(DateOnly date, CancellationToken ct = default)
    {
        var settings = await Row(ct);
        settings.LastDigestSentDate = date;
        await db.SaveChangesAsync(ct);
    }

    private async Task<ReportingSettings> Row(CancellationToken ct)
    {
        var settings = await db.ReportingSettings.FindAsync([1], ct);
        if (settings is null)
        {
            settings = new ReportingSettings { Id = 1 };
            db.ReportingSettings.Add(settings);
        }
        return settings;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

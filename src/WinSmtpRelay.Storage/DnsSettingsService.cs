using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class DnsSettingsService(
    RelayDbContext db,
    ICurrentActor actor,
    IAdminAuditService audit) : IDnsSettingsService
{
    public async Task<DnsSettings> GetAsync(CancellationToken ct = default)
        => await db.DnsSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct) ?? new DnsSettings();

    public async Task UpdateAsync(DnsSettings settings, CancellationToken ct = default)
    {
        // Every entry becomes an SPF ip4:/ip6: mechanism, so it must be an address or a CIDR network. A private
        // address is accepted (the self-check flags it) — refusing it would block saving the other fields.
        var sendingIps = SplitList(settings.SendingIpAddresses);
        var invalid = sendingIps.Where(ip => !IpAddressScope.TryParseAddressOrNetwork(ip, out _)).ToList();
        if (invalid.Count > 0)
            throw new InvalidOperationException(
                $"Not a valid IP address or CIDR network: {string.Join(", ", invalid)}.");

        var existing = await db.DnsSettings.FindAsync([1], ct);
        if (existing is null)
        {
            existing = new DnsSettings { Id = 1 };
            db.DnsSettings.Add(existing);
        }

        existing.PublicHostname = string.IsNullOrWhiteSpace(settings.PublicHostname) ? null : settings.PublicHostname.Trim();
        existing.SendingIpAddresses = string.Join("; ", sendingIps);
        existing.SpfIncludes = settings.SpfIncludes?.Trim() ?? "";
        existing.SpfAllQualifier = string.IsNullOrWhiteSpace(settings.SpfAllQualifier) ? "~all" : settings.SpfAllQualifier.Trim();
        existing.DmarcReportEmail = string.IsNullOrWhiteSpace(settings.DmarcReportEmail) ? null : settings.DmarcReportEmail.Trim();
        existing.DmarcPolicy = string.IsNullOrWhiteSpace(settings.DmarcPolicy) ? "none" : settings.DmarcPolicy.Trim();
        existing.DmarcPercentage = Math.Clamp(settings.DmarcPercentage, 1, 100);
        existing.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        // No cache: consumers read it on demand.

        await audit.WriteAsync(AdminAuditActions.DnsSettingsUpdated, actor,
            detail: $"hostname={existing.PublicHostname ?? "(none)"} sending IPs={(sendingIps.Count == 0 ? "(none)" : existing.SendingIpAddresses)}",
            ct: ct);
    }

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

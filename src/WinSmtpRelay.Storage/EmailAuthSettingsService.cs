using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Storage;

public class EmailAuthSettingsService(RelayDbContext db, IRuntimeConfigCache cache) : IEmailAuthSettingsService
{
    public async Task<EmailAuthSettings> GetAsync(CancellationToken ct = default)
        => await db.EmailAuthSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new EmailAuthSettings();

    public async Task UpdateAsync(
        bool spfEnabled,
        bool dmarcEnabled,
        EnforcementMode enforcement,
        bool requireSenderDomainVerification,
        bool requireRecipientDomainVerification,
        bool bindTenantToAllowIpRule,
        bool rejectUnresolvedTenant,
        CancellationToken ct = default)
    {
        var settings = await db.EmailAuthSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new EmailAuthSettings { Id = 1 };
            db.EmailAuthSettings.Add(settings);
        }

        settings.SpfEnabled = spfEnabled;
        settings.DmarcEnabled = dmarcEnabled;
        settings.Enforcement = enforcement;
        settings.RequireSenderDomainVerification = requireSenderDomainVerification;
        settings.RequireRecipientDomainVerification = requireRecipientDomainVerification;
        settings.BindTenantToAllowIpRule = bindTenantToAllowIpRule;
        settings.RejectUnresolvedTenant = rejectUnresolvedTenant;
        settings.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // The SMTP hot path caches these settings — refresh so the policy change takes effect now.
        cache.Invalidate();
    }
}

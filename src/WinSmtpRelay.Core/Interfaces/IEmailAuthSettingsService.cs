using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

public interface IEmailAuthSettingsService
{
    Task<EmailAuthSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Updates the inbound email-authentication policy and refreshes the SMTP-path cache.</summary>
    Task UpdateAsync(
        bool spfEnabled,
        bool dmarcEnabled,
        EnforcementMode enforcement,
        bool requireSenderDomainVerification,
        bool requireRecipientDomainVerification,
        bool bindTenantToAllowIpRule,
        bool rejectUnresolvedTenant,
        CancellationToken ct = default);
}

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Authorization;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// Data-protection token provider for passwordless "magic link" sign-in. It is registered as a separate,
/// named provider so its (short) token lifespan and data-protection purpose are isolated from the default
/// provider used by password reset / email confirmation, which keeps the 1-day default. A magic link
/// grants password-less access, so it must expire quickly (see <see cref="MagicLinkDefaults.TokenLifetime"/>).
/// </summary>
public sealed class MagicLinkTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<MagicLinkTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<AdminUser>> logger)
    : DataProtectorTokenProvider<AdminUser>(dataProtectionProvider, options, logger);

/// <summary>Options for <see cref="MagicLinkTokenProvider"/>; the short lifespan is set here.</summary>
public sealed class MagicLinkTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public MagicLinkTokenProviderOptions()
    {
        // Distinct data-protection purpose so these tokens can never be cross-validated with the default
        // provider's password-reset / email-confirmation tokens.
        Name = "WinSmtpRelay.MagicLinkTokenProvider";
        TokenLifespan = MagicLinkDefaults.TokenLifetime;
    }
}

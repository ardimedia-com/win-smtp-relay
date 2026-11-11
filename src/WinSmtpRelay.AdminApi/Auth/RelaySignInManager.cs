using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Storage.Identity;

namespace WinSmtpRelay.AdminApi.Auth;

/// <summary>
/// The relay's sign-in manager. It only exposes Identity's own "sign in, or hand off to two-factor" step for the
/// passwordless paths, so a sign-in link cannot skip the second factor of an account that enabled it (T1080):
/// <see cref="SignInManager{TUser}.SignInAsync(TUser, bool, string?)"/> would sign in directly, whereas this
/// stores the pending two-factor user exactly like a password sign-in does and lets the existing
/// <c>/account/login-2fa</c> flow finish it (a remembered device is honoured the same way).
/// </summary>
public sealed class RelaySignInManager(
    UserManager<AdminUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<AdminUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<AdminUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<AdminUser> confirmation)
    : SignInManager<AdminUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    /// <summary>
    /// Signs in after a passwordless proof (sign-in link) — or, when the account has two-factor enabled and this
    /// device is not remembered, returns <see cref="SignInResult.TwoFactorRequired"/> with the pending
    /// two-factor cookie set. The caller has already applied the lockout and usable-scope gates.
    /// </summary>
    public Task<SignInResult> PasswordlessSignInOrTwoFactorAsync(AdminUser user, bool isPersistent)
        => SignInOrTwoFactorAsync(user, isPersistent, loginProvider: null, bypassTwoFactor: false);
}

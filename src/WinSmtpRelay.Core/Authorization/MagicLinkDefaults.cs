namespace WinSmtpRelay.Core.Authorization;

/// <summary>
/// Shared constants for the passwordless "magic link" sign-in flow. The link is short-lived and
/// single-use: the token is verified by <c>UserManager.VerifyUserTokenAsync</c> and then invalidated by
/// rotating the user's security stamp. Kept in Core so both the token-provider registration (AdminApi)
/// and the sign-in pages (AdminUi) reference the same values without coupling those layers together.
/// </summary>
public static class MagicLinkDefaults
{
    /// <summary>Identity token-provider name; matches the <c>AddTokenProvider</c> registration.</summary>
    public const string ProviderName = "MagicLink";

    /// <summary>Token purpose — binds a generated token to magic-link sign-in only.</summary>
    public const string Purpose = "magic-link-signin";

    /// <summary>How long a sign-in link stays valid. Deliberately short — it grants password-less access.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
}

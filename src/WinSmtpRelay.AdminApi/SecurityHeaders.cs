using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace WinSmtpRelay.AdminApi;

/// <summary>
/// Browser hardening headers for every admin-plane response (UI, API, MCP): no MIME sniffing, no referrer leaking
/// tokens from account URLs, never framed (clickjacking), and a Content-Security-Policy that keeps the page to its
/// own origin while still running Blazor Server.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// The policy. Scripts, styles, fonts, images and the circuit's WebSocket stay on the admin origin. Inline
    /// script is still allowed because the app shell carries a theme bootstrap script, the import map and a few
    /// inline handlers (password reveal, tenant switcher, passkey buttons); moving those into files is what allows
    /// dropping <c>'unsafe-inline'</c> later. <c>frame-ancestors 'none'</c> is the CSP form of X-Frame-Options DENY.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; "
        + "script-src 'self' 'unsafe-inline'; "
        + "style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data:; "
        + "font-src 'self' data:; "
        + "connect-src 'self'; "
        + "object-src 'none'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'";

    public static IApplicationBuilder UseRelaySecurityHeaders(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                var headers = ctx.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                return Task.CompletedTask;
            });
            await next();
        });
}

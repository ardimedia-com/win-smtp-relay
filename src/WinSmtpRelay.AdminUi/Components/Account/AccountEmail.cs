using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Mail;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.AdminUi.Components.Account;

/// <summary>
/// Thin wrapper over <see cref="SystemEmail"/> for the account pages (signup verification, password
/// reset). Kept so the Razor call sites read naturally; all MIME composition (text + HTML
/// alternative) and CRLF sanitization live once in <see cref="SystemEmail"/>.
/// </summary>
internal static class AccountEmail
{
    public static Task EnqueueAsync(
        IMessageQueue queue, string from, string to, string subject, SystemEmailContent content,
        int tenantId = TenantDefaults.DefaultTenantId, CancellationToken ct = default)
        => SystemEmail.EnqueueAsync(queue, from, to, subject, content, tenantId, ct);
}

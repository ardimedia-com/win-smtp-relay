using System.Text;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.AdminUi.Components.Account;

/// <summary>
/// Composes a plain-text system email (signup verification, password reset) as RFC822 and queues
/// it through the relay's own delivery pipeline. Header values are sanitized against CRLF injection.
/// </summary>
internal static class AccountEmail
{
    public static async Task EnqueueAsync(
        IMessageQueue queue, string from, string to, string subject, string textBody,
        int tenantId = TenantDefaults.DefaultTenantId, CancellationToken ct = default)
    {
        from = Header(from);
        to = Header(to);
        subject = Header(subject);

        var messageId = $"<{Guid.NewGuid():N}@winsmtprelay>";
        var raw = Encoding.UTF8.GetBytes(
            $"From: {from}\r\n" +
            $"To: {to}\r\n" +
            $"Subject: {subject}\r\n" +
            $"Date: {DateTimeOffset.UtcNow:r}\r\n" +
            $"Message-ID: {messageId}\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            textBody);

        await queue.EnqueueAsync(new QueuedMessage
        {
            MessageId = messageId,
            Sender = from,
            Recipients = to,
            RawMessage = raw,
            SizeBytes = raw.Length,
            TenantId = tenantId,
            NextRetryUtc = DateTimeOffset.UtcNow
        }, ct);
    }

    private static string Header(string value) => value.Replace("\r", "").Replace("\n", "").Trim();
}

using System.Text;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Mail;

/// <summary>
/// Composes a plain-text system email — signup verification, password reset, the Setup test message,
/// and the reporting digest/alerts — as RFC822 and queues it through the relay's own delivery pipeline.
/// Header values are sanitized against CRLF so a hostile subject or address cannot inject additional
/// headers. This is the single composer for every internally-generated message; callers must not
/// hand-roll their own, so the sanitization and header set can never diverge between call sites.
/// </summary>
public static class SystemEmail
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

    /// <summary>Strips CR/LF (and trims) so a header value cannot inject extra headers.</summary>
    private static string Header(string value) => value.Replace("\r", "").Replace("\n", "").Trim();
}

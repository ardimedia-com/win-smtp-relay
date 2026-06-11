using System.Text;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Mail;

/// <summary>
/// Composes a system email — signup verification, password reset, the Setup test message, and the
/// reporting digest/alerts — as RFC822 multipart/alternative (plain text + HTML, both rendered from
/// the same <see cref="SystemEmailContent"/>) and queues it through the relay's own delivery pipeline.
/// Header values are sanitized against CRLF so a hostile subject or address cannot inject additional
/// headers. This is the single composer for every internally-generated message; callers must not
/// hand-roll their own, so the sanitization and header set can never diverge between call sites.
/// </summary>
public static class SystemEmail
{
    public static async Task EnqueueAsync(
        IMessageQueue queue, string from, string to, string subject, SystemEmailContent content,
        int tenantId = TenantDefaults.DefaultTenantId, CancellationToken ct = default)
    {
        from = Header(from);
        to = Header(to);
        subject = Header(subject);

        var messageId = $"<{Guid.NewGuid():N}@winsmtprelay>";
        var boundary = $"=_wsr_{Guid.NewGuid():N}";

        var textBody = RenderText(content);
        // Base64 keeps the HTML part within SMTP's 998-char line limit regardless of content length,
        // and is transport-safe on non-8BITMIME hops.
        var htmlBase64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(SystemEmailHtml.Render(content)),
            Base64FormattingOptions.InsertLineBreaks);

        var raw = Encoding.UTF8.GetBytes(
            $"From: {from}\r\n" +
            $"To: {to}\r\n" +
            $"Subject: {subject}\r\n" +
            $"Date: {DateTimeOffset.UtcNow:r}\r\n" +
            $"Message-ID: {messageId}\r\n" +
            "MIME-Version: 1.0\r\n" +
            $"Content-Type: multipart/alternative; boundary=\"{boundary}\"\r\n" +
            "\r\n" +
            $"--{boundary}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            textBody +
            "\r\n" +
            $"--{boundary}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            htmlBase64 + "\r\n" +
            $"--{boundary}--\r\n");

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

    /// <summary>Plain-text alternative, rendered from the same structured content as the HTML part.</summary>
    private static string RenderText(SystemEmailContent content)
    {
        var sb = new StringBuilder();
        foreach (var paragraph in content.Paragraphs)
            sb.Append(paragraph).Append("\r\n\r\n");
        if (!string.IsNullOrWhiteSpace(content.ActionUrl))
            sb.Append(content.ActionUrl).Append("\r\n\r\n");
        if (!string.IsNullOrWhiteSpace(content.MonospaceBlock))
            sb.Append(content.MonospaceBlock.TrimEnd()).Append("\r\n\r\n");
        foreach (var paragraph in content.ClosingParagraphs)
            sb.Append(paragraph).Append("\r\n\r\n");
        if (!string.IsNullOrWhiteSpace(content.FooterNote))
            sb.Append(content.FooterNote).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Strips CR/LF (and trims) so a header value cannot inject extra headers.</summary>
    private static string Header(string value) => value.Replace("\r", "").Replace("\n", "").Trim();
}

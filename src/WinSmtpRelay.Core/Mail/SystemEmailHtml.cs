using System.Text;

namespace WinSmtpRelay.Core.Mail;

/// <summary>
/// Renders the HTML alternative of an internally-generated email from <see cref="SystemEmailContent"/>.
/// Email-client-safe markup: table layout, inline CSS only, system fonts, a "bulletproof" CTA button
/// with a plain-text link fallback (the padding sits on the table cell so Outlook Classic's Word engine
/// renders it identically to modern clients). All dynamic text is HTML-encoded here, including the
/// action URL.
/// </summary>
public static class SystemEmailHtml
{
    private const string AppName = "WIN-SMTP-RELAY";

    public static string Render(SystemEmailContent content)
    {
        var html = new StringBuilder();

        html.Append("<!DOCTYPE html><html><body style=\"margin:0;padding:0;background-color:#f4f4f5;\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:#f4f4f5;padding:32px 0;\"><tr><td align=\"center\">");
        html.Append("<table role=\"presentation\" width=\"560\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;width:100%;background-color:#ffffff;border:1px solid #e4e4e7;border-radius:8px;font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;\">");

        // Branding header
        html.Append("<tr><td style=\"padding:20px 32px;border-bottom:1px solid #e4e4e7;\">");
        html.Append($"<span style=\"font-size:16px;font-weight:600;color:#18181b;letter-spacing:0.02em;\">{Encode(AppName)}</span>");
        html.Append("</td></tr>");

        // Body
        html.Append("<tr><td style=\"padding:32px;\">");
        html.Append($"<h1 style=\"margin:0 0 20px;font-size:20px;line-height:1.3;color:#18181b;\">{Encode(content.Title)}</h1>");
        foreach (var paragraph in content.Paragraphs)
            html.Append($"<p style=\"margin:0 0 12px;font-size:14px;line-height:1.6;color:#3f3f46;\">{Encode(paragraph)}</p>");

        if (!string.IsNullOrWhiteSpace(content.ButtonText) && !string.IsNullOrWhiteSpace(content.ActionUrl))
        {
            var encodedUrl = Encode(content.ActionUrl);
            html.Append("<div style=\"height:16px;line-height:16px;font-size:1px;\">&nbsp;</div>");
            html.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr>");
            html.Append("<td align=\"center\" bgcolor=\"#18181b\" style=\"background-color:#18181b;border-radius:6px;padding:12px 28px;\">");
            html.Append($"<a href=\"{encodedUrl}\" style=\"display:inline-block;color:#ffffff;font-size:14px;font-weight:600;text-decoration:none;\"><span style=\"color:#ffffff;\">{Encode(content.ButtonText)}</span></a>");
            html.Append("</td></tr></table>");
            html.Append("<div style=\"height:24px;line-height:24px;font-size:1px;\">&nbsp;</div>");
            html.Append("<p style=\"margin:0 0 4px;font-size:12px;line-height:1.5;color:#71717a;\">If the button does not work, open this link:</p>");
            html.Append($"<p style=\"margin:0 0 12px;font-size:12px;line-height:1.5;word-break:break-all;\"><a href=\"{encodedUrl}\" style=\"color:#2563eb;text-decoration:underline;\">{encodedUrl}</a></p>");
        }

        if (!string.IsNullOrWhiteSpace(content.MonospaceBlock))
        {
            html.Append("<p style=\"margin:4px 0 12px;font-size:13px;line-height:1.6;font-family:Consolas,'Courier New',monospace;background-color:#f4f4f5;border:1px solid #e4e4e7;border-radius:6px;padding:14px 20px;color:#18181b;\">");
            html.Append(EncodePreformatted(content.MonospaceBlock));
            html.Append("</p>");
        }

        foreach (var paragraph in content.ClosingParagraphs)
            html.Append($"<p style=\"margin:0 0 12px;font-size:14px;line-height:1.6;color:#3f3f46;\">{Encode(paragraph)}</p>");

        html.Append("</td></tr>");

        // Footer
        if (!string.IsNullOrWhiteSpace(content.FooterNote))
        {
            html.Append("<tr><td style=\"padding:16px 32px;border-top:1px solid #e4e4e7;\">");
            html.Append($"<p style=\"margin:0;font-size:12px;line-height:1.5;color:#a1a1aa;\">{Encode(content.FooterNote)}</p>");
            html.Append("</td></tr>");
        }

        html.Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    private static string Encode(string value) => System.Net.WebUtility.HtmlEncode(value);

    /// <summary>
    /// Preserves line breaks and multi-space alignment of a preformatted block without relying on
    /// white-space CSS (which Outlook Classic ignores): newlines become &lt;br/&gt; and space runs
    /// become non-breaking pairs, while single spaces still allow prose lines to wrap.
    /// </summary>
    private static string EncodePreformatted(string value) =>
        Encode(value.TrimEnd())
            .Replace("\r\n", "\n")
            .Replace("\n", "<br/>")
            .Replace("  ", "&nbsp;&nbsp;");
}

using System.Text;
using MimeKit;
using MimeKit.Utils;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Delivery.Dsn;

/// <summary>What a delivery status notification reports.</summary>
/// <param name="ReportingMta">The relay's host name (Reporting-MTA).</param>
/// <param name="OriginalSender">The envelope sender of the failed message — the DSN's recipient.</param>
/// <param name="OriginalMessageId">The Message-ID of the failed message.</param>
/// <param name="ArrivalUtc">When the relay accepted the failed message.</param>
/// <param name="OriginalRaw">The failed message as stored; only its header block is attached.</param>
/// <param name="Recipients">The recipients that were not delivered.</param>
/// <param name="NowUtc">The time the report is made.</param>
public sealed record DsnInput(
    string ReportingMta,
    string OriginalSender,
    string OriginalMessageId,
    DateTimeOffset ArrivalUtc,
    byte[] OriginalRaw,
    IReadOnlyList<DsnRecipient> Recipients,
    DateTimeOffset NowUtc);

/// <summary>
/// Builds an RFC 3464 delivery status notification: <c>multipart/report; report-type=delivery-status</c> with a
/// human-readable first part, a <c>message/delivery-status</c> part (one per-message group, one group per failed
/// recipient), and the original message's headers (<c>text/rfc822-headers</c>) — headers only, so the DSN never
/// carries the content back. Pure: no I/O.
/// </summary>
public static class DsnComposer
{
    /// <summary>Upper bound for the attached header block, so a message with huge headers cannot bloat the DSN.</summary>
    private const int MaxHeaderBytes = 64 * 1024;

    /// <summary>Upper bound for one Diagnostic-Code value.</summary>
    private const int MaxDiagnosticLength = 900;

    public static MimeMessage Compose(DsnInput input)
    {
        var senderDomain = DomainOf(input.OriginalSender);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Mail Delivery System", $"MAILER-DAEMON@{senderDomain}"));
        message.To.Add(MailboxAddress.Parse(input.OriginalSender));
        message.Subject = "Undelivered Mail Returned to Sender";
        message.Date = input.NowUtc;
        message.MessageId = MimeUtils.GenerateMessageId(input.ReportingMta);
        // RFC 3834: an automatic response — auto-responders must not answer it.
        message.Headers.Add("Auto-Submitted", "auto-replied");

        var report = new MultipartReport("delivery-status");
        report.Add(new TextPart("plain") { Text = HumanReadable(input) });

        var status = new MessageDeliveryStatus();
        var perMessage = new HeaderList
        {
            { "Reporting-MTA", $"dns; {input.ReportingMta}" },
            { "Arrival-Date", DateUtils.FormatDate(input.ArrivalUtc) },
        };
        status.StatusGroups.Add(perMessage);
        foreach (var r in input.Recipients)
        {
            var group = new HeaderList
            {
                { "Final-Recipient", $"rfc822; {r.Address}" },
                { "Action", "failed" },
                { "Status", StatusOf(r) },
            };
            if (HostOf(r.RemoteServer) is { } host)
                group.Add("Remote-MTA", $"dns; {host}");
            group.Add("Diagnostic-Code", $"smtp; {Diagnostic(r)}");
            group.Add("Last-Attempt-Date", DateUtils.FormatDate(input.NowUtc));
            status.StatusGroups.Add(group);
        }
        report.Add(status);

        // text/rfc822-headers: the original header block parsed as a body-less message (MimeKit keeps the raw
        // header values), so the sender can see which of their messages this is about.
        using (var headerStream = new MemoryStream(HeaderBlock(input.OriginalRaw)))
            report.Add(new TextRfc822Headers { Message = MimeMessage.Load(headerStream) });

        message.Body = report;
        return message;
    }

    /// <summary>The DSN as bytes, ready to queue.</summary>
    public static byte[] ComposeBytes(DsnInput input)
    {
        using var stream = new MemoryStream();
        Compose(input).WriteTo(FormatOptions.Default, stream);
        return stream.ToArray();
    }

    /// <summary>
    /// True when <paramref name="raw"/> is itself a delivery report (<c>multipart/report; report-type=delivery-status</c>)
    /// — never answered with another DSN, whatever its envelope sender says.
    /// </summary>
    public static bool IsDeliveryReport(byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(HeaderBlock(raw));
            var headers = HeaderList.Load(stream);
            var contentType = headers[HeaderId.ContentType];
            return contentType is not null
                && contentType.Contains("multipart/report", StringComparison.OrdinalIgnoreCase)
                && contentType.Contains("delivery-status", StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The RFC 3463 status of a recipient: the enhanced code from its reply, else one derived from the reply class.</summary>
    internal static string StatusOf(DsnRecipient r)
    {
        if (r.Suppressed)
            return "5.7.1";
        if (SuppressionPolicy.ExtractEnhancedCode(r.StatusMessage) is { } enhanced)
            return enhanced;
        return r.StatusCode?.StartsWith('4') == true ? "4.0.0" : "5.0.0";
    }

    private static string Diagnostic(DsnRecipient r)
    {
        var text = r.Suppressed
            ? "550 5.7.1 Not attempted: the address is on the suppression list of the sending relay (an earlier hard bounce or a complaint)"
            : $"{r.StatusCode ?? "554"} {r.StatusMessage}".Trim();
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length > MaxDiagnosticLength ? text[..MaxDiagnosticLength] : text;
    }

    private static string HumanReadable(DsnInput input)
    {
        var sb = new StringBuilder();
        sb.Append("This is the mail system at host ").Append(input.ReportingMta).Append(".\r\n\r\n");
        sb.Append("Your message could not be delivered to one or more recipients. The relay will not try again.\r\n\r\n");
        foreach (var r in input.Recipients)
        {
            sb.Append("  <").Append(r.Address).Append(">: ").Append(Diagnostic(r)).Append("\r\n");
            if (HostOf(r.RemoteServer) is { } host)
                sb.Append("    (reported by ").Append(host).Append(")\r\n");
        }
        sb.Append("\r\nOriginal message: ").Append(input.OriginalMessageId)
            .Append(", received ").Append(input.ArrivalUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture))
            .Append(". Its headers are attached.\r\n");
        return sb.ToString();
    }

    /// <summary>The header block of a raw message (up to the first empty line), capped at <see cref="MaxHeaderBytes"/>.</summary>
    internal static byte[] HeaderBlock(byte[] raw)
    {
        var end = IndexOfBlankLine(raw);
        var length = Math.Min(end < 0 ? raw.Length : end, MaxHeaderBytes);
        if (length > 0 && raw[length - 1] == '\n')
            return raw[..length];
        // Cut inside a line (cap reached, or no line end at all): close the last line.
        return [.. raw[..length], (byte)'\r', (byte)'\n'];
    }

    private static int IndexOfBlankLine(byte[] raw)
    {
        for (var i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] == '\n' && raw[i + 1] == '\n')
                return i + 1;
            if (raw[i] == '\n' && raw[i + 1] == '\r' && i + 2 < raw.Length && raw[i + 2] == '\n')
                return i + 1;
        }
        return -1;
    }

    private static string? HostOf(string? remoteServer)
    {
        if (string.IsNullOrWhiteSpace(remoteServer))
            return null;
        var colon = remoteServer.LastIndexOf(':');
        return colon > 0 && remoteServer.IndexOf(':') == colon ? remoteServer[..colon] : remoteServer;
    }

    private static string DomainOf(string address) => address[(address.LastIndexOf('@') + 1)..];
}

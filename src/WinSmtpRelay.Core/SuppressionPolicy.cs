using System.Text.RegularExpressions;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core;

/// <summary>
/// Decides whether a failed recipient goes on the suppression list (T1075). Only a rejection that says the address
/// does not exist qualifies: a policy block, a blocklisting of our IP, a full mailbox or a smart host's refusal says
/// nothing about the mailbox, and suppressing on it would silently stop mail to valid addresses.
/// <para>
/// Qualifies: a RCPT TO rejection by the recipient's own mail server with enhanced status 5.1.1, 5.1.10 or another
/// 5.1.x address status (except 5.1.7 / 5.1.8, which are about the sender), or 5.2.1 (mailbox disabled); or — when
/// no enhanced status is given — a plain 550 / 551 / 553. In every case the response text must not carry policy
/// wording (<see cref="PolicyWords"/>), and any 5.7.x disqualifies.
/// </para>
/// </summary>
public static partial class SuppressionPolicy
{
    /// <summary>
    /// Words that mark a rejection as a policy / reputation decision rather than a statement about the mailbox.
    /// The one-time data migration <c>SuppressionPolicyAndQueueLease</c> lifts existing entries by the same list;
    /// keep the two in step.
    /// </summary>
    public static readonly IReadOnlyList<string> PolicyWords =
    [
        "policy", "block", "blacklist", "spam", "reputation", "rbl", "spamhaus", "barracuda", "denied",
        "not permitted", "prohibited", "authenticat", "spf", "dmarc", "dkim", "rate limit", "too many", "quota",
        "temporarily",
    ];

    /// <summary>True when <paramref name="result"/> should put the recipient on the suppression list.</summary>
    public static bool ShouldSuppress(DeliveryResult result) =>
        result is { RecipientRejected: true, Success: false, ViaSmartHost: false }
        && ShouldSuppress(result.StatusCode, result.StatusMessage);

    /// <summary>True when an RCPT rejection with this reply code and text says the address does not exist.</summary>
    public static bool ShouldSuppress(string statusCode, string? statusMessage)
    {
        if (!statusCode.StartsWith('5'))
            return false;

        var text = statusMessage ?? "";
        if (HasPolicyWording(text))
            return false;

        var enhanced = EnhancedCode().Match(text);
        if (enhanced.Success)
        {
            if (enhanced.Groups["class"].Value != "5")
                return false;
            var subject = enhanced.Groups["subject"].Value;
            var detail = enhanced.Groups["detail"].Value;
            return subject switch
            {
                "1" => detail is not ("7" or "8"),   // 5.1.x address status; 5.1.7/5.1.8 concern the sender
                "2" => detail == "1",                // 5.2.1 mailbox disabled
                _ => false,                          // 5.7.x policy, 5.2.2 full, 5.4.x routing, 5.5.x protocol, ...
            };
        }

        return statusCode is "550" or "551" or "553";
    }

    /// <summary>True when the text reads like a policy, reputation or rate decision.</summary>
    public static bool HasPolicyWording(string text) =>
        PolicyWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))
        || EnhancedPolicyCode().IsMatch(text);

    /// <summary>The first RFC 3463 enhanced status code in a server response ("5.1.1"), or null.</summary>
    public static string? ExtractEnhancedCode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var m = EnhancedCode().Match(text);
        return m.Success ? m.Value : null;
    }

    [GeneratedRegex(@"(?<![\d.])(?<class>[245])\.(?<subject>\d{1,3})\.(?<detail>\d{1,3})(?![\d.])")]
    private static partial Regex EnhancedCode();

    [GeneratedRegex(@"(?<![\d.])[45]\.7\.\d{1,3}(?![\d.])")]
    private static partial Regex EnhancedPolicyCode();
}

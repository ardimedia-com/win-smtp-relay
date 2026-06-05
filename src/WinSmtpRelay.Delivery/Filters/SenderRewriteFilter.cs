using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MimeKit;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Delivery.Filters;

public class SenderRewriteFilter : IMessageFilter
{
    private readonly IRuntimeConfigCache _configCache;
    private readonly ILogger<SenderRewriteFilter> _logger;

    public SenderRewriteFilter(IRuntimeConfigCache configCache, ILogger<SenderRewriteFilter> logger)
    {
        _configCache = configCache;
        _logger = logger;
    }

    public int Order => 200;

    public async Task<MessageFilterResult> FilterAsync(MessageFilterContext context, CancellationToken cancellationToken = default)
    {
        // Rewrite rules are tenant-owned; the cache loads them across all tenants, so apply only the
        // message's own tenant's rules (otherwise one tenant's rule could rewrite another's mail).
        var rules = (await _configCache.GetSenderRewriteRulesAsync(cancellationToken))
            .Where(r => r.TenantId == context.TenantId)
            .ToList();
        if (rules.Count == 0)
            return MessageFilterResult.Accepted();

        var matchingRule = rules.FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(r.FromPattern) &&
            Regex.IsMatch(context.Sender, r.FromPattern, RegexOptions.IgnoreCase));

        if (matchingRule == null)
            return MessageFilterResult.Accepted();

        _logger.LogInformation("Rewriting sender from {OldSender} to {NewSender} (rule #{RuleId}, pattern: {Pattern})",
            context.Sender, matchingRule.ToAddress, matchingRule.Id, matchingRule.FromPattern);

        var mimeMessage = await MimeMessage.LoadAsync(new MemoryStream(context.RawMessage), cancellationToken);
        mimeMessage.From.Clear();
        mimeMessage.From.Add(MailboxAddress.Parse(matchingRule.ToAddress));

        context.Sender = matchingRule.ToAddress;

        using var output = new MemoryStream();
        await mimeMessage.WriteToAsync(output, cancellationToken);
        return MessageFilterResult.AcceptedWithModification(output.ToArray());
    }
}

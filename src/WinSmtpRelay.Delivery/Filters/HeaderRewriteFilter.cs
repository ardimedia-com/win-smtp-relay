using Microsoft.Extensions.Logging;
using MimeKit;
using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Delivery.Filters;

public class HeaderRewriteFilter : IMessageFilter
{
    private readonly IRuntimeConfigCache _configCache;
    private readonly ILogger<HeaderRewriteFilter> _logger;

    public HeaderRewriteFilter(IRuntimeConfigCache configCache, ILogger<HeaderRewriteFilter> logger)
    {
        _configCache = configCache;
        _logger = logger;
    }

    public int Order => 100;

    public async Task<MessageFilterResult> FilterAsync(MessageFilterContext context, CancellationToken cancellationToken = default)
    {
        // Rewrite rules are tenant-owned; the cache loads them across all tenants, so apply only the
        // message's own tenant's rules (otherwise one tenant's rule could alter another's mail).
        var rules = (await _configCache.GetHeaderRewriteRulesAsync(cancellationToken))
            .Where(r => r.TenantId == context.TenantId)
            .ToList();
        if (rules.Count == 0)
            return MessageFilterResult.Accepted();

        var mimeMessage = await MimeMessage.LoadAsync(new MemoryStream(context.RawMessage), cancellationToken);
        var modified = false;

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.HeaderName)) continue;

            switch (rule.Action.ToLowerInvariant())
            {
                case "remove":
                    if (mimeMessage.Headers.Contains(rule.HeaderName))
                    {
                        mimeMessage.Headers.RemoveAll(rule.HeaderName);
                        modified = true;
                        _logger.LogInformation("Removed header {Header} (rule #{RuleId})", rule.HeaderName, rule.Id);
                    }
                    break;

                case "set":
                    if (!string.IsNullOrWhiteSpace(rule.NewValue))
                    {
                        if (rule.MatchValue == null || mimeMessage.Headers[rule.HeaderName] == rule.MatchValue)
                        {
                            mimeMessage.Headers[rule.HeaderName] = rule.NewValue;
                            modified = true;
                            _logger.LogInformation("Set header {Header} = {Value} (rule #{RuleId})", rule.HeaderName, rule.NewValue, rule.Id);
                        }
                    }
                    break;

                case "append":
                    if (!string.IsNullOrWhiteSpace(rule.NewValue))
                    {
                        mimeMessage.Headers.Add(rule.HeaderName, rule.NewValue);
                        modified = true;
                        _logger.LogInformation("Appended header {Header} = {Value} (rule #{RuleId})", rule.HeaderName, rule.NewValue, rule.Id);
                    }
                    break;
            }
        }

        if (!modified)
            return MessageFilterResult.Accepted();

        using var output = new MemoryStream();
        await mimeMessage.WriteToAsync(output, cancellationToken);
        return MessageFilterResult.AcceptedWithModification(output.ToArray());
    }
}

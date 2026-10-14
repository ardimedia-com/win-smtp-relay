namespace WinSmtpRelay.Core.Health;

/// <summary>A sending service behind a smart host, with the DKIM selectors its domain authentication publishes.</summary>
public sealed record SmartHostProvider(string Name, IReadOnlyList<string> DkimSelectors);

/// <summary>
/// Known smart-host providers. When all outbound mail leaves through one of them, DMARC passes on the
/// provider's DKIM signature for the sender domain — which exists once the domain is authenticated at the
/// provider, i.e. once its selectors are published under <c>&lt;selector&gt;._domainkey.&lt;domain&gt;</c>.
/// </summary>
public static class SmartHostProviders
{
    private static readonly (string HostSuffix, SmartHostProvider Provider)[] Known =
    [
        ("brevo.com", new("Brevo", ["brevo1", "brevo2", "mail"])),
        ("sendinblue.com", new("Brevo", ["brevo1", "brevo2", "mail"])),
        ("sendgrid.net", new("SendGrid", ["s1", "s2"])),
        ("mandrillapp.com", new("Mailchimp Transactional", ["mte1", "mte2"])),
        ("mailjet.com", new("Mailjet", ["mailjet"])),
        ("office365.com", new("Microsoft 365", ["selector1", "selector2"])),
        ("outlook.com", new("Microsoft 365", ["selector1", "selector2"])),
    ];

    /// <summary>The provider a smart host belongs to, or null when it is not a known one.</summary>
    public static SmartHostProvider? Find(string? smartHost)
    {
        if (string.IsNullOrWhiteSpace(smartHost))
            return null;
        var host = smartHost.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var (suffix, provider) in Known)
        {
            if (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal))
                return provider;
        }
        return null;
    }
}

using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Delivery.Tests;

internal class StubRuntimeConfigCache : IRuntimeConfigCache
{
    public List<string> AcceptedDomains { get; set; } = [];
    public List<string> AcceptedSenderDomains { get; set; } = [];
    public List<IpAccessRule> IpAccessRules { get; set; } = [];
    public List<DomainRoute> DomainRoutes { get; set; } = [];
    public List<HeaderRewriteEntry> HeaderRewriteRules { get; set; } = [];
    public List<SenderRewriteEntry> SenderRewriteRules { get; set; } = [];

    public Task<IReadOnlyList<string>> GetAcceptedDomainsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(AcceptedDomains);

    public Task<IReadOnlyList<string>> GetAcceptedSenderDomainsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(AcceptedSenderDomains);

    public Task<IReadOnlyList<IpAccessRule>> GetIpAccessRulesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IpAccessRule>>(IpAccessRules);

    public Task<IReadOnlyList<DomainRoute>> GetDomainRoutesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DomainRoute>>(DomainRoutes);

    public Task<IReadOnlyList<HeaderRewriteEntry>> GetHeaderRewriteRulesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HeaderRewriteEntry>>(HeaderRewriteRules);

    public Task<IReadOnlyList<SenderRewriteEntry>> GetSenderRewriteRulesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SenderRewriteEntry>>(SenderRewriteRules);

    public void Invalidate() { }
}

using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Delivery;

/// <summary>
/// Chooses which due message to deliver next (M14): tenants take turns (round robin), so one tenant's backlog does
/// not delay every other tenant, and no destination domain gets more than <see cref="PerDomainCap"/> messages in
/// delivery at once, so a slow domain cannot occupy every slot. Also the authority on what this process is
/// delivering right now — the lease sweep skips those. Thread-safe.
/// </summary>
internal sealed class DeliveryScheduler(int perDomainCap)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _perDomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string[]> _inFlight = [];
    private int? _lastTenant;

    public int PerDomainCap { get; } = Math.Max(1, perDomainCap);

    /// <summary>The messages this process is delivering right now.</summary>
    public IReadOnlyCollection<long> InFlightIds
    {
        get { lock (_gate) return [.. _inFlight.Keys]; }
    }

    /// <summary>
    /// The due tenants in the order to try them: the tenants after the one served last, then the rest from the
    /// start — so every tenant with due mail gets a turn before any tenant gets a second one.
    /// </summary>
    public IReadOnlyList<int> TenantOrder(IReadOnlyList<int> dueTenants)
    {
        int? last;
        lock (_gate) last = _lastTenant;
        var ordered = dueTenants.Distinct().OrderBy(t => t).ToList();
        if (last is null)
            return ordered;
        return [.. ordered.Where(t => t > last), .. ordered.Where(t => t <= last)];
    }

    /// <summary>True when every destination domain of <paramref name="candidate"/> is below the per-domain cap.</summary>
    public bool CanStart(QueueCandidate candidate)
    {
        var domains = DomainsOf(candidate.Recipients);
        lock (_gate)
            return !_inFlight.ContainsKey(candidate.Id)
                && domains.All(d => _perDomain.GetValueOrDefault(d) < PerDomainCap);
    }

    /// <summary>Records that delivery of a message started.</summary>
    public void Started(long messageId, int tenantId, string recipients)
    {
        var domains = DomainsOf(recipients);
        lock (_gate)
        {
            _inFlight[messageId] = domains;
            foreach (var d in domains)
                _perDomain[d] = _perDomain.GetValueOrDefault(d) + 1;
            _lastTenant = tenantId;
        }
    }

    /// <summary>Records that delivery of a message ended (successfully or not).</summary>
    public void Finished(long messageId)
    {
        lock (_gate)
        {
            if (!_inFlight.Remove(messageId, out var domains))
                return;
            foreach (var d in domains)
            {
                var n = _perDomain.GetValueOrDefault(d) - 1;
                if (n <= 0)
                    _perDomain.Remove(d);
                else
                    _perDomain[d] = n;
            }
        }
    }

    internal static string[] DomainsOf(string recipients) =>
        [.. RecipientList.Split(recipients)
            .Select(r => r[(r.LastIndexOf('@') + 1)..].ToLowerInvariant())
            .Distinct()];
}

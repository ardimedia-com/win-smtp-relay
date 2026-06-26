using WinSmtpRelay.Core.Health;

namespace WinSmtpRelay.Service.HealthChecks;

/// <summary>
/// A single self-check. Each implementation probes one area (deliverability, connectivity, certificates,
/// the queue, …) and returns zero or more findings. Implementations are scoped services resolved together
/// by the <see cref="HealthCheckRunner"/>; a check that throws is turned into one Error finding by the
/// runner, so a single failure never aborts the whole run.
/// </summary>
public interface IHealthCheck
{
    /// <summary>Short name for logging which check ran/failed.</summary>
    string Name { get; }

    Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct);
}

/// <summary>
/// A transient (not persisted) finding produced by a check. The runner maps these to
/// <see cref="Core.Models.HealthCheckFinding"/> rows. <paramref name="Code"/> + <paramref name="Target"/>
/// identify the finding across runs so a newly-appeared Error can trigger an immediate alert.
/// </summary>
public sealed record HealthFinding(
    string Category,
    string Code,
    HealthSeverity Severity,
    string Title,
    string Detail,
    string? Target = null,
    string? Hint = null);

/// <summary>
/// Convenience base for checks of a single <see cref="Category"/>: provides per-severity finding factories
/// so check bodies read as <c>Err("code", "title", "detail")</c> instead of repeating the category and
/// severity on every line.
/// </summary>
public abstract class HealthCheckBase : IHealthCheck
{
    public abstract string Name { get; }

    /// <summary>The category every finding from this check belongs to (one of <see cref="HealthCategories"/>).</summary>
    protected abstract string Category { get; }

    public abstract Task<IReadOnlyList<HealthFinding>> RunAsync(CancellationToken ct);

    protected HealthFinding Ok(string code, string title, string detail, string? target = null) =>
        new(Category, code, HealthSeverity.Ok, title, detail, target);

    protected HealthFinding Info(string code, string title, string detail, string? target = null, string? hint = null) =>
        new(Category, code, HealthSeverity.Info, title, detail, target, hint);

    protected HealthFinding Warn(string code, string title, string detail, string? target = null, string? hint = null) =>
        new(Category, code, HealthSeverity.Warning, title, detail, target, hint);

    protected HealthFinding Err(string code, string title, string detail, string? target = null, string? hint = null) =>
        new(Category, code, HealthSeverity.Error, title, detail, target, hint);
}

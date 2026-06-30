namespace WinSmtpRelay.Core.Interfaces;

/// <summary>Number of rows purged by a retention run, per category. <see cref="ResendBodiesStripped"/> counts
/// partially-delivered messages whose body was dropped after the resend window (the metadata rows stay).</summary>
public readonly record struct RetentionPurgeResult(int Messages, int DeliveryLogs, int Suppressions, int ResendBodiesStripped);

/// <summary>
/// Applies the host data-retention policy: deletes terminal queued messages, delivery-log rows, and
/// (optionally) suppression entries past their configured windows. Runs from the nightly maintenance
/// task in an unscoped (all-tenants) context. Statistics aggregates are purged separately by the
/// statistics aggregator.
/// </summary>
public interface IRetentionService
{
    Task<RetentionPurgeResult> RunPurgeAsync(CancellationToken ct = default);
}

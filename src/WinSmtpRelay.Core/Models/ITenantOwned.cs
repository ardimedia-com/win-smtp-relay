namespace WinSmtpRelay.Core.Models;

/// <summary>
/// Marker for entities partitioned by tenant. A tenant-scoped query filter is applied to
/// all implementers; new rows default to <see cref="TenantDefaults.DefaultTenantId"/>.
/// </summary>
public interface ITenantOwned
{
    int TenantId { get; set; }
}

using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.Core.Interfaces;

/// <summary>
/// Computes the setup readiness of the current tenant (a live checklist of required,
/// recommended, and optional configuration). Reads the ambient <see cref="ICurrentTenant"/>;
/// returns a host-scope result (no items) when no single tenant is in scope. DNS publication
/// is intentionally NOT included here — it needs a live lookup and is composed by the page.
/// </summary>
public interface ITenantReadinessService
{
    Task<TenantReadiness> GetAsync(CancellationToken ct = default);
}

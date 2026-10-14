namespace WinSmtpRelay.Core;

/// <summary>
/// A host-level caller (host-level API key, no tenant named) tried to change tenant data while more than
/// one tenant exists. Tenant-owned entities default to the Default tenant, so without this refusal the
/// change would silently land there. The message names the tenants so the caller can repeat the call with
/// one of them (REST: the <c>X-Relay-Tenant</c> header; MCP: the <c>tenant</c> argument).
/// </summary>
public sealed class TenantRequiredException(IReadOnlyList<(int Id, string Slug)> tenants)
    : InvalidOperationException(
        "This change belongs to one tenant, but none is selected and several exist "
        + $"({string.Join(", ", tenants.Select(t => $"{t.Id} = {t.Slug}"))}). "
        + "Name the tenant: REST header 'X-Relay-Tenant: <id or slug>', MCP argument 'tenant'.")
{
    public IReadOnlyList<(int Id, string Slug)> Tenants { get; } = tenants;
}

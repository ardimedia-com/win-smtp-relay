using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WinSmtpRelay.Core.Authorization;

namespace WinSmtpRelay.AdminApi.Mcp;

/// <summary>
/// Hosts the MCP server inside the admin plane: same HTTPS listener, same API-key authentication,
/// same role policies and capability scopes, same audit attribution — an MCP session is just another
/// programmatic client of the relay.
/// </summary>
public static class RelayMcpExtensions
{
    public static IServiceCollection AddRelayMcp(this IServiceCollection services)
    {
        services.AddMcpServer()
            // Stateless: no per-session server state, so requests need no Mcp-Session-Id affinity and
            // a service restart cannot strand sessions. Every tool call runs in its own authenticated
            // HTTP request scope (tenant context + audit actor set by the middleware).
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<RelayMcpTools>();
        return services;
    }

    public static IEndpointRouteBuilder MapRelayMcp(this IEndpointRouteBuilder endpoints)
    {
        // Baseline: any authenticated admin identity (View). Each tool then re-applies the exact role
        // policy + API-key scope of its REST equivalent (see RelayMcpTools.RequireAsync).
        endpoints.MapMcp("/mcp").RequireAuthorization(AuthorizationPolicies.AdminView);
        return endpoints;
    }
}

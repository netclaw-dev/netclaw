using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Netclaw.Daemon.Security;

/// <summary>
/// Registers the Netclaw multi-scheme auth pipeline: a PolicyScheme selector
/// that routes to DeviceBearer when an <c>Authorization: Bearer</c> header is
/// present, otherwise to Loopback (local operator).
/// </summary>
internal static class NetclawAuthExtensions
{
    internal static IServiceCollection AddNetclawAuthSchemes(this IServiceCollection services)
    {
        services
            .AddAuthentication("AuthSelector")
            .AddPolicyScheme("AuthSelector", "Bearer or Loopback selector", options =>
            {
                options.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.ContainsKey("Authorization") &&
                    ctx.Request.Headers.Authorization.ToString()
                        .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? DeviceTokenAuthenticationHandler.SchemeName
                        : LoopbackAuthenticationHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, LoopbackAuthenticationHandler>(
                LoopbackAuthenticationHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(
                DeviceTokenAuthenticationHandler.SchemeName, _ => { });

        return services;
    }
}

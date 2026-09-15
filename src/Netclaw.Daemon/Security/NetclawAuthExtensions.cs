// -----------------------------------------------------------------------
// <copyright file="NetclawAuthExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Security;

/// <summary>
/// Registers the Netclaw multi-scheme auth pipeline: a PolicyScheme selector
/// that routes an active Teams activity endpoint to its SDK scheme. It routes
/// other bearer requests to DeviceBearer and local requests to Loopback.
/// </summary>
internal static class NetclawAuthExtensions
{
    internal static IServiceCollection AddNetclawAuthSchemes(this IServiceCollection services, DaemonConfig daemonConfig)
    {
        services.TryAddSingleton(daemonConfig);
        services
            .AddAuthentication("AuthSelector")
            .AddPolicyScheme("AuthSelector", "Bearer or Loopback selector", options =>
            {
                options.ForwardDefaultSelector = ctx =>
                {
                    var teamsIngress = ctx.RequestServices.GetService<TeamsIngressRegistration>();
                    if (teamsIngress?.CanActivateSdk == true
                        && ctx.Request.Path == TeamsActivityEndpointExtensions.ActivityPath)
                    {
                        return TeamsActivityEndpointExtensions.AuthenticationScheme;
                    }

                    return ctx.Request.Headers.ContainsKey("Authorization")
                           && ctx.Request.Headers.Authorization.ToString()
                               .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? DeviceTokenAuthenticationHandler.SchemeName
                        : LoopbackAuthenticationHandler.SchemeName;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, LoopbackAuthenticationHandler>(
                LoopbackAuthenticationHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(
                DeviceTokenAuthenticationHandler.SchemeName, _ => { });

        return services;
    }
}

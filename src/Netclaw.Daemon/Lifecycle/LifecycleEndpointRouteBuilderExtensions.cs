// -----------------------------------------------------------------------
// <copyright file="LifecycleEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Netclaw.Daemon.Services;

namespace Netclaw.Daemon.Lifecycle;

public static class LifecycleEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        // Daemon lifecycle endpoint — CLI calls this before sending SIGTERM.
        // Config-triggered restart coordination happens inside DaemonRestartCoordinator.
        app.MapPost("/api/lifecycle/shutdown", (
            DaemonLifecycleNotifier notifier,
            HttpRequest request) =>
        {
            var reason = request.Query["reason"].ToString();
            if (string.IsNullOrEmpty(reason))
                return Results.BadRequest(new { error = "reason query parameter is required" });

            notifier.NotifyShutdown(reason);
            return Results.Ok(new { reason, pid = Environment.ProcessId });
        }).RequireAuthorization();

        return app;
    }
}

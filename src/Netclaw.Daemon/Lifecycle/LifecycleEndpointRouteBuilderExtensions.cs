// -----------------------------------------------------------------------
// <copyright file="LifecycleEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Netclaw.Daemon.Services;

namespace Netclaw.Daemon.Lifecycle;

/// <summary>Request to shut down the daemon, sourced from query string.</summary>
public sealed record ShutdownDaemonRequest([FromQuery(Name = "reason")] string? Reason);

/// <summary>Successful shutdown acknowledgement: echoes the reason and reports the daemon PID.</summary>
public sealed record ShutdownDaemonResponse(string Reason, int Pid);

/// <summary>Error payload returned when a lifecycle request is malformed.</summary>
public sealed record LifecycleErrorResponse(string Error);

public static class LifecycleEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        // Daemon lifecycle endpoint — CLI calls this before sending SIGTERM.
        // Config-triggered restart coordination happens inside DaemonRestartCoordinator.
        app.MapPost("/api/lifecycle/shutdown", Results<Ok<ShutdownDaemonResponse>, BadRequest<LifecycleErrorResponse>> (
            [AsParameters] ShutdownDaemonRequest request,
            DaemonLifecycleNotifier notifier) =>
        {
            if (string.IsNullOrEmpty(request.Reason))
                return TypedResults.BadRequest(new LifecycleErrorResponse("reason query parameter is required"));

            notifier.NotifyShutdown(request.Reason);
            return TypedResults.Ok(new ShutdownDaemonResponse(request.Reason, Environment.ProcessId));
        })
        .WithName("ShutdownDaemon")
        .WithSummary("Request a graceful daemon shutdown ahead of SIGTERM.")
        .WithTags("Lifecycle")
        .RequireAuthorization();

        return app;
    }
}

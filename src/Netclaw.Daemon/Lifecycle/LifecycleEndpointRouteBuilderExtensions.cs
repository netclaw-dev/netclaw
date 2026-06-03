// -----------------------------------------------------------------------
// <copyright file="LifecycleEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
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

        // Self-shutdown endpoint. Unlike /shutdown above (which only notifies
        // observers and expects the caller to follow up with SIGTERM), this
        // endpoint asks the host to stop itself. Used by callers that cannot
        // signal the process directly — notably Netclaw.Web, which manages
        // the daemon via HTTP only.
        app.MapPost("/api/lifecycle/stop", (
            DaemonLifecycleNotifier notifier,
            IHostApplicationLifetime lifetime,
            HttpRequest request) =>
        {
            var reason = request.Query["reason"].ToString();
            if (string.IsNullOrEmpty(reason)) reason = "web-stop";

            notifier.NotifyShutdown(reason);

            // Schedule the actual stop on the ThreadPool so the HTTP response
            // can flush before the host begins tearing down Kestrel.
            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                lifetime.StopApplication();
            });

            return Results.Accepted(value: new { reason, pid = Environment.ProcessId });
        }).RequireAuthorization();

        return app;
    }
}

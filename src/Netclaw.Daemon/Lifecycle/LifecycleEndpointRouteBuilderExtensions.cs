// -----------------------------------------------------------------------
// <copyright file="LifecycleEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Security;

namespace Netclaw.Daemon.Lifecycle;

/// <summary>
/// Request to shut down the daemon, sourced from query string. <c>pid</c> and <c>home</c> name the
/// daemon that the caller means to stop; they stay text here so the endpoint can answer 400 for a bad value.
/// </summary>
public sealed record ShutdownDaemonRequest(
    [FromQuery(Name = "reason")] string? Reason,
    [FromQuery(Name = "pid")] string? Pid,
    [FromQuery(Name = "home")] string? Home);

/// <summary>Successful shutdown acknowledgement: echoes the reason and reports the daemon PID.</summary>
public sealed record ShutdownDaemonResponse(string Reason, int Pid, string Home);

/// <summary>Error payload returned when a lifecycle request is malformed.</summary>
public sealed record LifecycleErrorResponse(string Error);

public static class LifecycleEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        // Daemon lifecycle endpoint — CLI calls this before sending SIGTERM.
        // Config-triggered restart coordination happens inside DaemonRestartCoordinator.
        app.MapPost("/api/lifecycle/shutdown", Results<Ok<ShutdownDaemonResponse>, BadRequest<LifecycleErrorResponse>, Conflict<LifecycleErrorResponse>> (
            [AsParameters] ShutdownDaemonRequest request,
            DaemonLifecycleNotifier notifier,
            NetclawPaths paths,
            ILogger<DaemonLifecycleNotifier> logger) =>
        {
            if (string.IsNullOrEmpty(request.Reason))
                return TypedResults.BadRequest(new LifecycleErrorResponse("reason query parameter is required"));

            // The notice must name this daemon. A CLI for one home must not make the daemon of another
            // home log a stop or send the daemon.stopping alert (#2527). A missing value is no match.
            // shutdown-identity:begin
            if (!int.TryParse(request.Pid, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || pid <= 0
                || string.IsNullOrEmpty(request.Home)
                || !HomeIdentity.IsFullPath(request.Home))
                return TypedResults.BadRequest(new LifecycleErrorResponse("pid and home query parameters are required"));

            if (pid != Environment.ProcessId || !HomeIdentity.Same(request.Home, paths.BasePath))
            {
                // The home comes from the query string: strip line breaks before it reaches the log.
                var noticeHome = LogSafe.Text(request.Home);
                var message = $"This daemon is PID {Environment.ProcessId} for home {paths.BasePath}; the notice is for PID {pid} and home {noticeHome}.";
                logger.LogWarning("Refused a shutdown notice. {Detail}", message);
                return TypedResults.Conflict(new LifecycleErrorResponse(message));
            }
            // shutdown-identity:end

            notifier.NotifyShutdown(request.Reason);
            return TypedResults.Ok(new ShutdownDaemonResponse(request.Reason, Environment.ProcessId, HomeIdentity.Canonical(paths.BasePath)));
        })
        .WithName("ShutdownDaemon")
        .WithSummary("Request a graceful daemon shutdown ahead of SIGTERM.")
        .WithTags("Lifecycle")
        .RequireAuthorization();

        return app;
    }
}

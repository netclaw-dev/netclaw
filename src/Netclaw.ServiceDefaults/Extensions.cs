// -----------------------------------------------------------------------
// <copyright file="Extensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Netclaw.ServiceDefaults;

/// <summary>
/// Shared Aspire ServiceDefaults entry point.
///
/// The project exists so that future production-observability work has a
/// canonical hook to flesh out (OpenTelemetry, health checks, resilience,
/// service discovery). It is intentionally NOT wired into
/// <c>Netclaw.Daemon</c> by this change — that requires its own perf and
/// regression validation and lives in a separate PR.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers Aspire-aware defaults on the host builder. Currently a
    /// no-op placeholder; future work will configure OpenTelemetry,
    /// health checks, HTTP resilience, and service discovery here.
    /// </summary>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        return builder;
    }

    /// <summary>
    /// Maps the conventional Aspire health endpoints (<c>/health</c>,
    /// <c>/alive</c>) on the application's pipeline. Currently a no-op
    /// placeholder for the same reason as <see cref="AddServiceDefaults"/>.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        return app;
    }
}

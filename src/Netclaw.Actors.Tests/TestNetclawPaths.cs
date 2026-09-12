// -----------------------------------------------------------------------
// <copyright file="TestNetclawPaths.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;

namespace Netclaw.Actors.Tests;

/// <summary>
/// Test helper that registers a <see cref="NetclawPaths"/> singleton rooted at
/// a unique temp directory. Used by Akka.Hosting.TestKit fixtures that need
/// <c>SessionServices</c> to construct but do not exercise real filesystem
/// behavior. The directory is not cleaned up automatically; tests relying on
/// durability should manage their own temp dirs.
/// </summary>
internal static class TestNetclawPaths
{
    public static IServiceCollection AddTestNetclawPaths(this IServiceCollection services)
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        return services.AddSingleton(new NetclawPaths(basePath));
    }
}

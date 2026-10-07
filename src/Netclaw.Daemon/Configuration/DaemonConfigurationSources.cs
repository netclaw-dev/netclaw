// -----------------------------------------------------------------------
// <copyright file="DaemonConfigurationSources.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

public static class DaemonConfigurationSources
{
    /// <summary>
    /// Adds the daemon configuration sources. A later source overrides an earlier source:
    /// netclaw.json, then secrets.json, then <c>NETCLAW_*</c> environment variables.
    /// </summary>
    public static IConfigurationBuilder AddNetclawDaemonSources(this IConfigurationBuilder builder, NetclawPaths paths)
        => builder
            .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .AddJsonFile(paths.SecretsPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NETCLAW_");
}

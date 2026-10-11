// -----------------------------------------------------------------------
// <copyright file="TestEnvironmentInitializer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;

namespace Netclaw.Cli.Tests;

internal static class TestEnvironmentInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Disable config file watching in test hosts.
        // Reminder, pairing, daemon-client, and MCP OAuth tests build real
        // host instances, which enable file watchers for appsettings.json
        // reload by default. Running many of them in parallel exhausts the
        // inotify watch limit on Linux.
        Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
    }
}

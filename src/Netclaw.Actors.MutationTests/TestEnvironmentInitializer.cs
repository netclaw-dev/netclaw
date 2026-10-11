// -----------------------------------------------------------------------
// <copyright file="TestEnvironmentInitializer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;

namespace Netclaw.Actors.MutationTests;

internal static class TestEnvironmentInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Disable config file watching in test hosts.
        // Mutation tests spin up Akka.Hosting hosts, which enable file
        // watchers for appsettings.json reload by default. Stryker runs each
        // mutant repeatedly, so a shared opt-out avoids inotify exhaustion on
        // Linux.
        Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
    }
}

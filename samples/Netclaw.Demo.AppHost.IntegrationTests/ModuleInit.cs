// -----------------------------------------------------------------------
// <copyright file="ModuleInit.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;

namespace Netclaw.Demo.AppHost.IntegrationTests;

/// <summary>
/// Disables host-builder config reload watchers across the entire test
/// process before any test infrastructure spins up. Aspire's hosted apps
/// register file watchers on Linux via inotify; under
/// <c>DistributedApplicationTestingBuilder</c> the watcher fanout can
/// exhaust the per-user inotify limit and surface as
/// <c>System.IO.IOException: The configured user limit (128) on the
/// number of inotify instances has been reached</c>. The Aspire
/// integration-testing skill prescribes this opt-out — see
/// <c>dotnet-skills:aspire-integration-testing</c>.
/// </summary>
internal static class ModuleInit
{
    [ModuleInitializer]
    public static void Init()
    {
        Environment.SetEnvironmentVariable(
            "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE",
            "false");
    }
}

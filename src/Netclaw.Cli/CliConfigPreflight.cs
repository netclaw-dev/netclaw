// -----------------------------------------------------------------------
// <copyright file="CliConfigPreflight.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli;

internal static class CliConfigPreflight
{
    public const string MissingConfigMessage = "daemon not configured - please run netclaw init";

    public static bool TryWriteMissingChatConfig(
        NetclawPaths paths,
        string mode,
        bool chatJsonOutput,
        TextWriter writer,
        out int exitCode)
    {
        if (mode is not ("chat" or "headless"))
        {
            exitCode = 0;
            return false;
        }

        return TryWriteMissingConfig(
            paths,
            jsonOutput: mode == "headless" && chatJsonOutput,
            writer,
            out exitCode);
    }

    public static bool TryWriteMissingConfig(
        NetclawPaths paths,
        bool jsonOutput,
        TextWriter writer,
        out int exitCode)
    {
        if (File.Exists(paths.NetclawConfigPath))
        {
            exitCode = 0;
            return false;
        }

        // A missing local daemon config only means "not initialized" when THIS
        // machine hosts the daemon. When the caller explicitly targets a remote
        // daemon (NETCLAW_DAEMON_ENDPOINT) or this install is a paired client
        // (client.json endpoint), the daemon's configuration lives on the
        // daemon host — blocking here regressed paired/remote clients and the
        // containerized eval harness after #1540. These are exactly the
        // endpoint sources DaemonApi.ResolveEndpoint prefers over the local
        // daemon config.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT"))
            || ClientConfigFile.ReadEndpoint(paths) is not null)
        {
            exitCode = 0;
            return false;
        }

        if (jsonOutput)
        {
            var node = new JsonObject
            {
                ["overall"] = "not-configured",
                ["message"] = MissingConfigMessage,
            };
            writer.WriteLine(node.ToJsonString(JsonDefaults.Indented));
        }
        else
        {
            writer.WriteLine(MissingConfigMessage);
        }

        exitCode = 1;
        return true;
    }
}

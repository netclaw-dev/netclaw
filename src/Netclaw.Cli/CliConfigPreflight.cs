// -----------------------------------------------------------------------
// <copyright file="CliConfigPreflight.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli;

internal static class CliConfigPreflight
{
    public const string MissingConfigMessage = "daemon not configured - please run netclaw init";

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

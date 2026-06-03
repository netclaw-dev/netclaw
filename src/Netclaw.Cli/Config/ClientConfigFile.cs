// -----------------------------------------------------------------------
// <copyright file="ClientConfigFile.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Config;

internal sealed class ClientConfigFile
{
    public string? Endpoint { get; init; }

    public static void WriteEndpoint(NetclawPaths paths, string endpoint)
    {
        var dir = Path.GetDirectoryName(paths.ClientConfigPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        File.WriteAllText(
            paths.ClientConfigPath,
            JsonSerializer.Serialize(new ClientConfigFile { Endpoint = endpoint.TrimEnd('/') }, JsonDefaults.Indented));
    }
}

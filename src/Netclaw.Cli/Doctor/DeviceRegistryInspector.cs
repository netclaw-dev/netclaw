// -----------------------------------------------------------------------
// <copyright file="DeviceRegistryInspector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

internal static class DeviceRegistryInspector
{
    public static int CountPairedDevices(NetclawPaths paths)
    {
        if (!File.Exists(paths.DevicesPath))
            return 0;

        try
        {
            return JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(paths.DevicesPath))?.Count ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}

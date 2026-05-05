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
            using var doc = JsonDocument.Parse(File.ReadAllText(paths.DevicesPath));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}

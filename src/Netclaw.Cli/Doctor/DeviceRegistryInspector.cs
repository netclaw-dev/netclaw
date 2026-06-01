// -----------------------------------------------------------------------
// <copyright file="DeviceRegistryInspector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

internal static class DeviceRegistryInspector
{
    public static DeviceRegistrySnapshot Read(NetclawPaths paths)
    {
        var devicesFileExists = File.Exists(paths.DevicesPath);
        var devices = ReadDevices(paths);
        var (hasLocalDeviceToken, localTokenMatchesDevice) = ReadLocalDeviceTokenState(paths, devices);
        var hasCompletedBootstrap = new BootstrapStateStore(paths).HasCompletedNonLocalBootstrap();

        return new DeviceRegistrySnapshot(
            devices.Count,
            devicesFileExists,
            hasLocalDeviceToken,
            localTokenMatchesDevice,
            hasCompletedBootstrap);
    }

    private static List<PairedDevice> ReadDevices(NetclawPaths paths)
    {
        if (!File.Exists(paths.DevicesPath))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(paths.DevicesPath));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<PairedDevice>>(doc.RootElement.GetRawText()) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static (bool HasLocalDeviceToken, bool LocalTokenMatchesDevice) ReadLocalDeviceTokenState(
        NetclawPaths paths,
        IReadOnlyList<PairedDevice> devices)
    {
        if (!File.Exists(paths.SecretsPath))
            return (false, false);

        var secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
        if (!secrets.TryGetValue("DeviceToken", out var rawValue))
            return (false, false);

        var token = rawValue is JsonElement jsonElement ? jsonElement.GetString() : rawValue?.ToString();
        token = ConfigFileHelper.DecryptIfEncrypted(paths, token);
        if (string.IsNullOrWhiteSpace(token))
            return (false, false);

        foreach (var device in devices)
        {
            if (PairedDevice.VerifyToken(token, device))
                return (true, true);
        }

        return (true, false);
    }
}

internal sealed record DeviceRegistrySnapshot(
    int DeviceCount,
    bool DevicesFileExists,
    bool HasLocalDeviceToken,
    bool LocalTokenMatchesDevice,
    bool HasCompletedBootstrap);

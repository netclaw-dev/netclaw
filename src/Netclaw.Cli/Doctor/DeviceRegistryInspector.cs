// -----------------------------------------------------------------------
// <copyright file="DeviceRegistryInspector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

internal static class DeviceRegistryInspector
{
    public static DeviceRegistrySnapshot Read(NetclawPaths paths)
    {
        var devices = ReadDevices(paths);
        var localTokenMatchesDevice = HasMatchingLocalDeviceToken(paths, devices);
        var hasCompletedBootstrap = new BootstrapStateStore(paths).HasCompletedNonLocalBootstrap();

        return new DeviceRegistrySnapshot(
            devices.Count,
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

    private static bool HasMatchingLocalDeviceToken(NetclawPaths paths, IReadOnlyList<PairedDevice> devices)
    {
        if (!File.Exists(paths.SecretsPath))
            return false;

        var secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
        if (!secrets.TryGetValue("DeviceToken", out var rawValue))
            return false;

        var token = rawValue is JsonElement jsonElement ? jsonElement.GetString() : rawValue?.ToString();
        token = ConfigFileHelper.DecryptIfEncrypted(paths, token);
        if (string.IsNullOrWhiteSpace(token))
            return false;

        foreach (var device in devices)
        {
            if (VerifyToken(token, device))
                return true;
        }

        return false;
    }

    private static bool VerifyToken(string rawToken, PairedDevice device)
    {
        try
        {
            var tokenBytes = Base64Url.DecodeFromChars(rawToken);
            var saltBytes = Convert.FromHexString(device.Salt);
            Span<byte> combined = stackalloc byte[tokenBytes.Length + saltBytes.Length];
            tokenBytes.CopyTo(combined);
            saltBytes.CopyTo(combined[tokenBytes.Length..]);
            var computed = Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
            return string.Equals(computed, device.TokenHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed record DeviceRegistrySnapshot(
    int DeviceCount,
    bool LocalTokenMatchesDevice,
    bool HasCompletedBootstrap);

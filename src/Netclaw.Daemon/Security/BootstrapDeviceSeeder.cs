// -----------------------------------------------------------------------
// <copyright file="BootstrapDeviceSeeder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Daemon.Security;

/// <summary>
/// Seeds a one-shot local paired device/token before the first successful non-local
/// daemon start when no other remote authentication path exists yet.
/// </summary>
internal sealed class BootstrapDeviceSeeder
{
    private readonly NetclawPaths _paths;
    private readonly DeviceRegistry _deviceRegistry;
    private readonly BootstrapStateStore _bootstrapStateStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BootstrapDeviceSeeder> _logger;
    private readonly ISecretsProtector _protector;

    public BootstrapDeviceSeeder(
        NetclawPaths paths,
        DeviceRegistry deviceRegistry,
        BootstrapStateStore bootstrapStateStore,
        TimeProvider timeProvider,
        ILogger<BootstrapDeviceSeeder> logger,
        ISecretsProtector protector)
    {
        _paths = paths;
        _deviceRegistry = deviceRegistry;
        _bootstrapStateStore = bootstrapStateStore;
        _timeProvider = timeProvider;
        _logger = logger;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public async Task<bool> EnsureSeededAsync(DaemonConfig config, CancellationToken cancellationToken)
    {
        if (!config.ExposureMode.RequiresRemoteAuthentication())
            return false;

        if (_bootstrapStateStore.HasCompletedNonLocalBootstrap())
            return false;

        var devices = await _deviceRegistry.ListAsync(cancellationToken);
        if (devices.Count > 0)
            return false;

        var existingSecrets = LoadSecrets();
        if (HasDeviceToken(existingSecrets))
            return false;

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Base64Url.EncodeToString(tokenBytes);
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
        var tokenHash = PairedDevice.ComputeTokenHash(rawToken, saltHex);
        var now = _timeProvider.GetUtcNow();

        var device = new PairedDevice
        {
            Name = $"{Environment.MachineName}-bootstrap",
            IsBootstrapDevice = true,
            TokenHash = tokenHash,
            Salt = saltHex,
            CreatedAt = now,
            LastUsedAt = now,
        };

        await _deviceRegistry.AddAsync(device, cancellationToken);
        existingSecrets["DeviceToken"] = rawToken;
        SecretsFileWriter.Write(_paths.SecretsPath, existingSecrets, _protector);
        _logger.LogInformation(
            "Seeded bootstrap paired device '{DeviceName}' for first non-local daemon start.",
            device.Name);
        return true;
    }

    public void MarkCompleted()
        => _bootstrapStateStore.MarkCompleted(_timeProvider);

    private Dictionary<string, object> LoadSecrets()
    {
        if (!File.Exists(_paths.SecretsPath))
            return new Dictionary<string, object> { ["configVersion"] = 1 };

        try
        {
            var text = File.ReadAllText(_paths.SecretsPath);
            var decrypted = SecretsFileWriter.DecryptJsonLeaves(text, _protector);
            return JsonSerializer.Deserialize<Dictionary<string, object>>(decrypted)
                ?? new Dictionary<string, object> { ["configVersion"] = 1 };
        }
        catch (JsonException)
        {
            return new Dictionary<string, object> { ["configVersion"] = 1 };
        }
    }

    private static bool HasDeviceToken(Dictionary<string, object> secrets)
    {
        return secrets.TryGetValue("DeviceToken", out var existing)
            && existing?.ToString() is { Length: > 0 };
    }
}

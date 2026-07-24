// -----------------------------------------------------------------------
// <copyright file="BootstrapDeviceSeeder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
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
    private readonly ISecretsProtector? _protector;

    public BootstrapDeviceSeeder(
        NetclawPaths paths,
        DeviceRegistry deviceRegistry,
        BootstrapStateStore bootstrapStateStore,
        TimeProvider timeProvider,
        ILogger<BootstrapDeviceSeeder> logger,
        ISecretsProtector? protector = null)
    {
        _paths = paths;
        _deviceRegistry = deviceRegistry;
        _bootstrapStateStore = bootstrapStateStore;
        _timeProvider = timeProvider;
        _logger = logger;
        _protector = protector;
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

        if (HasDeviceToken())
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
        var committed = false;
        try
        {
            committed = SecretsFileWriter.Update<bool>(
                _paths.SecretsPath,
                (root, _) =>
                {
                    if (root.TryGetPropertyValue("DeviceToken", out var existing)
                        && existing?.ToString() is { Length: > 0 })
                        return (null, false);

                    root["configVersion"] ??= 1;
                    root["DeviceToken"] = rawToken;
                    return (root, true);
                },
                protector: _protector,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            await RollBackDeviceAsync(device.Name, ex);
            throw;
        }

        if (!committed)
        {
            await _deviceRegistry.RemoveAsync(device.Name, CancellationToken.None);
            return false;
        }

        _logger.LogInformation(
            "Seeded bootstrap paired device '{DeviceName}' for first non-local daemon start.",
            device.Name);
        return true;
    }

    public void MarkCompleted()
        => _bootstrapStateStore.MarkCompleted(_timeProvider);

    private async Task RollBackDeviceAsync(string deviceName, Exception persistenceException)
    {
        try
        {
            await _deviceRegistry.RemoveAsync(deviceName, CancellationToken.None);
        }
        catch (Exception rollbackException)
        {
            throw new InvalidOperationException(
                "Failed to persist bootstrap device token and failed to roll back the paired device.",
                new AggregateException(persistenceException, rollbackException));
        }
    }

    private bool HasDeviceToken()
    {
        if (!File.Exists(_paths.SecretsPath))
            return false;

        var tokenFound = SecretsFileWriter.Update<bool>(
            _paths.SecretsPath,
            (root, _) =>
            {
                var found = root.TryGetPropertyValue("DeviceToken", out var existing)
                            && existing?.ToString() is { Length: > 0 };
                return (null, found);
            },
            protector: _protector);

        return tokenFound;
    }
}

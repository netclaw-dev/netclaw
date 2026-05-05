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
        if (devices.Count > 0 || HasLocalDeviceToken())
            return false;

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Base64Url.EncodeToString(tokenBytes);
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
        var tokenHash = DeviceRegistry.ComputeTokenHash(rawToken, saltHex);
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
        WriteLocalDeviceToken(rawToken);
        _logger.LogInformation(
            "Seeded bootstrap paired device '{DeviceName}' for first non-local daemon start.",
            device.Name);
        return true;
    }

    public void MarkCompleted()
        => _bootstrapStateStore.MarkCompleted(_timeProvider);

    private bool HasLocalDeviceToken()
    {
        if (!File.Exists(_paths.SecretsPath))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
            return doc.RootElement.TryGetProperty("DeviceToken", out var tokenElement)
                   && tokenElement.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(tokenElement.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void WriteLocalDeviceToken(string rawToken)
    {
        Dictionary<string, object> secrets;
        if (File.Exists(_paths.SecretsPath))
        {
            var text = File.ReadAllText(_paths.SecretsPath);
            var decrypted = _protector is not null
                ? SecretsFileWriter.DecryptJsonLeaves(text, _protector)
                : text;
            secrets = JsonSerializer.Deserialize<Dictionary<string, object>>(decrypted)
                ?? new Dictionary<string, object> { ["configVersion"] = 1 };
        }
        else
        {
            secrets = new Dictionary<string, object> { ["configVersion"] = 1 };
        }

        if (secrets.TryGetValue("DeviceToken", out var existing)
            && existing?.ToString() is { Length: > 0 })
        {
            return;
        }

        secrets["DeviceToken"] = rawToken;
        SecretsFileWriter.Write(_paths.SecretsPath, secrets, protector: _protector);
    }
}

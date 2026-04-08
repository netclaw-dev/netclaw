using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

/// <summary>
/// File-backed registry of devices that have been granted remote access via the pairing flow.
/// Persists to <c>~/.netclaw/config/devices.json</c>.
///
/// <para>Token verification hashes the presented raw token with each device's salt using
/// <c>SHA256(token_bytes || salt_bytes)</c> and compares against the stored hash.
/// The raw token is never stored on the daemon side.</para>
/// </summary>
internal sealed class DeviceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _devicesPath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeviceRegistry> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // In-memory cache — invalidated on AddAsync/RemoveAsync/UpdateLastUsedAsync writes.
    // Safe because devices.json is only modified by local operator actions through this class.
    private List<PairedDevice>? _cachedDevices;

    public DeviceRegistry(
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<DeviceRegistry> logger)
    {
        _devicesPath = paths.DevicesPath;
        _timeProvider = timeProvider;
        _logger = logger;

        var dir = Path.GetDirectoryName(_devicesPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Returns all currently registered devices.
    /// </summary>
    public async Task<IReadOnlyList<PairedDevice>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return new List<PairedDevice>(await ReadDevicesAsync(ct));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Adds a device to the registry. The caller is responsible for generating
    /// the token hash and salt before calling this method.
    /// </summary>
    public async Task AddAsync(PairedDevice device, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var devices = await ReadDevicesAsync(ct);

            if (devices.Any(existing =>
                string.Equals(existing.Name, device.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"A paired device named '{device.Name}' already exists. Revoke it before pairing again.");
            }

            var updated = new List<PairedDevice>(devices) { device };
            await WriteDevicesAsync(updated, ct);
            _logger.LogInformation("Registered new paired device '{Name}'.", device.Name);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes a device by name (case-insensitive).
    /// Returns <c>true</c> if the device was found and removed, <c>false</c> if not found.
    /// </summary>
    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var devices = await ReadDevicesAsync(ct);
            var updated = devices
                .Where(d => !string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (updated.Count == devices.Count)
                return false;

            await WriteDevicesAsync(updated, ct);
            _logger.LogInformation("Removed paired device '{Name}'.", name);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Looks up a device by verifying the presented raw token against each device's
    /// stored hash (<c>SHA256(token_bytes || salt_bytes)</c>).
    /// Returns the matching <see cref="PairedDevice"/> or <c>null</c> if no match found.
    /// </summary>
    public async Task<PairedDevice?> LookupByTokenAsync(string rawToken, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var devices = await ReadDevicesAsync(ct);
            foreach (var device in devices)
            {
                if (VerifyToken(rawToken, device))
                    return device;
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Atomically looks up a device by token and updates its <c>LastUsedAt</c> timestamp.
    /// Single lock acquisition, single file read, and one conditional write.
    /// Returns the matched device or <c>null</c>.
    /// </summary>
    public async Task<PairedDevice?> LookupAndUpdateLastUsedAsync(string rawToken, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var devices = await ReadDevicesAsync(ct);
            PairedDevice? matched = null;
            foreach (var device in devices)
            {
                if (VerifyToken(rawToken, device))
                {
                    matched = device;
                    break;
                }
            }

            if (matched is null)
                return null;

            var now = _timeProvider.GetUtcNow();
            var updated = devices
                .Select(d => ReferenceEquals(d, matched) ? d with { LastUsedAt = now } : d)
                .ToList();
            await WriteDevicesAsync(updated, ct);
            return matched;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Updates the <c>LastUsedAt</c> timestamp for the named device.
    /// No-op if the device is not found (skips write).
    /// </summary>
    public async Task UpdateLastUsedAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var devices = await ReadDevicesAsync(ct);
            if (!devices.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                return;

            var now = _timeProvider.GetUtcNow();
            var updated = devices
                .Select(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)
                    ? d with { LastUsedAt = now }
                    : d)
                .ToList();

            await WriteDevicesAsync(updated, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Computes the token hash for a given raw token and salt, using
    /// <c>SHA256(token_bytes || salt_bytes)</c>.
    /// </summary>
    /// <param name="rawToken">Base64url-encoded raw token.</param>
    /// <param name="saltHex">Lowercase hex-encoded salt.</param>
    /// <returns>Lowercase hex-encoded SHA-256 digest.</returns>
    public static string ComputeTokenHash(string rawToken, string saltHex)
    {
        var tokenBytes = Base64Url.DecodeFromChars(rawToken);
        var saltBytes = Convert.FromHexString(saltHex);
        Span<byte> combined = stackalloc byte[tokenBytes.Length + saltBytes.Length];
        tokenBytes.CopyTo(combined);
        saltBytes.CopyTo(combined[tokenBytes.Length..]);
        return Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
    }

    private static bool VerifyToken(string rawToken, PairedDevice device)
    {
        try
        {
            var computed = ComputeTokenHash(rawToken, device.Salt);
            return string.Equals(computed, device.TokenHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            // Malformed token bytes (bad base64url or hex) — treat as no-match
            return false;
        }
    }

    private async Task<List<PairedDevice>> ReadDevicesAsync(CancellationToken ct)
    {
        if (_cachedDevices is not null)
            return _cachedDevices;

        try
        {
            var json = await File.ReadAllTextAsync(_devicesPath, ct);
            _cachedDevices = JsonSerializer.Deserialize<List<PairedDevice>>(json, JsonOptions) ?? [];
        }
        catch (FileNotFoundException)
        {
            _cachedDevices = [];
        }

        return _cachedDevices;
    }

    private async Task WriteDevicesAsync(List<PairedDevice> devices, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(devices, JsonOptions);
        await File.WriteAllTextAsync(_devicesPath, json, ct);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(_devicesPath))
            File.SetUnixFileMode(_devicesPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _cachedDevices = devices;
    }
}

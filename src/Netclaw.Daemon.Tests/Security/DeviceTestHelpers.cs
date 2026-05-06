// -----------------------------------------------------------------------
// <copyright file="DeviceTestHelpers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;

namespace Netclaw.Daemon.Tests.Security;

/// <summary>
/// Shared factory for creating <see cref="PairedDevice"/> instances with
/// random tokens for testing. Returns the raw token (for auth header) and
/// the fully-hashed device record (for registry seeding).
/// </summary>
internal static class DeviceTestHelpers
{
    internal static (string RawToken, PairedDevice Device) MakeDevice(
        string name, DateTimeOffset createdAt, bool isBootstrapDevice = false)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var rawToken = Base64Url.EncodeToString(tokenBytes);
        var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
        var tokenHash = PairedDevice.ComputeTokenHash(rawToken, saltHex);

        var device = new PairedDevice
        {
            Name = name,
            IsBootstrapDevice = isBootstrapDevice,
            TokenHash = tokenHash,
            Salt = saltHex,
            CreatedAt = createdAt,
            LastUsedAt = createdAt,
        };
        return (rawToken, device);
    }
}

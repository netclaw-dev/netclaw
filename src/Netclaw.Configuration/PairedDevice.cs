// -----------------------------------------------------------------------
// <copyright file="PairedDevice.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Netclaw.Configuration;

/// <summary>
/// A device that has been granted remote access to the daemon via the pairing flow.
/// Stored in <c>~/.netclaw/config/devices.json</c>.
/// The raw token is never stored — only <c>SHA256(token_bytes || salt_bytes)</c> is persisted.
/// </summary>
public sealed record PairedDevice
{
    /// <summary>
    /// Human-readable device name (e.g., <c>aaron-laptop</c>).
    /// Used as the <c>SenderId</c> claim when the device authenticates.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// True only for the daemon-owned one-shot bootstrap device seeded before the
    /// first successful non-local daemon start.
    /// </summary>
    public bool IsBootstrapDevice { get; init; }

    /// <summary>
    /// Lowercase hex-encoded SHA-256 digest of the concatenation
    /// <c>token_bytes || salt_bytes</c>. Never the raw token.
    /// </summary>
    public string TokenHash { get; init; } = string.Empty;

    /// <summary>
    /// Lowercase hex-encoded random salt bytes used when computing <see cref="TokenHash"/>.
    /// Per-device salt ensures two devices with the same token would still produce different hashes.
    /// </summary>
    public string Salt { get; init; } = string.Empty;

    /// <summary>
    /// When the device was first paired (UTC).
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the device token was last successfully validated (UTC).
    /// Updated on every successful authentication.
    /// </summary>
    public DateTimeOffset LastUsedAt { get; init; }

    /// <summary>
    /// Computes <c>SHA256(token_bytes || salt_bytes)</c>.
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

    /// <summary>
    /// Verifies a raw token against this device's stored hash and salt.
    /// Returns <c>false</c> for malformed tokens instead of throwing.
    /// </summary>
    public static bool VerifyToken(string rawToken, PairedDevice device)
    {
        try
        {
            var computed = ComputeTokenHash(rawToken, device.Salt);
            return string.Equals(computed, device.TokenHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

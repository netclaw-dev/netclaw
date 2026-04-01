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
}

namespace Netclaw.Configuration;

/// <summary>
/// Sanitized view of a paired device for CLI display.
/// Does not include <c>TokenHash</c> or <c>Salt</c> — only the information
/// an operator needs to manage their device list.
/// </summary>
public sealed record PairedDeviceInfoDto(string Name, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt);

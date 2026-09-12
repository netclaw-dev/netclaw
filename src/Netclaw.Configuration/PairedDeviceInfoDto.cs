// -----------------------------------------------------------------------
// <copyright file="PairedDeviceInfoDto.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Sanitized view of a paired device for CLI display.
/// Does not include <c>TokenHash</c> or <c>Salt</c> — only the information
/// an operator needs to manage their device list.
/// </summary>
public sealed record PairedDeviceInfoDto(string Name, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt);

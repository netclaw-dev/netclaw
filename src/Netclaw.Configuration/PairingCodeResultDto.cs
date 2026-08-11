// -----------------------------------------------------------------------
// <copyright file="PairingCodeResultDto.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Result returned by the <c>GeneratePairingCode</c> SignalR hub method.
/// Contains the formatted code for display and its expiration time.
/// </summary>
public sealed record PairingCodeResultDto(string FormattedCode, DateTimeOffset ExpiresAt);

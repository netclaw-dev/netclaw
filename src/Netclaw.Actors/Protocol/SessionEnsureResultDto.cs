// -----------------------------------------------------------------------
// <copyright file="SessionEnsureResultDto.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Wire-safe response for ensuring a SignalR session binding.
/// </summary>
/// <param name="Created">
/// True when a new session was created; false when existing session was reattached.
/// </param>
public sealed record SessionEnsureResultDto(string SessionId, bool Created);

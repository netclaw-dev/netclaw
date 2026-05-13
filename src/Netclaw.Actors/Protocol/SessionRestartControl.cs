// -----------------------------------------------------------------------
// <copyright file="SessionRestartControl.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Requests that a live session stop accepting new work and passivate for coordinated daemon restart.
/// </summary>
public sealed record PrepareForDaemonRestart : IWithSessionId, INoSerializationVerificationNeeded
{
    public required SessionId SessionId { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// Rehydrates a session after coordinated daemon restart and primes a one-turn continuity notice.
/// </summary>
public sealed record WarmSession : IWithSessionId, INoSerializationVerificationNeeded
{
    public required SessionId SessionId { get; init; }

    public required string RestartNotice { get; init; }
}

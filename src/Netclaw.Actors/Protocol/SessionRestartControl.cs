// -----------------------------------------------------------------------
// <copyright file="SessionRestartControl.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Requests that a live session stop accepting new work and passivate for coordinated daemon restart.
/// </summary>
public sealed record PrepareForDaemonRestart(SessionId SessionId, string Reason)
    : IWithSessionId, INoSerializationVerificationNeeded;

/// <summary>
/// Rehydrates a session after coordinated daemon restart and primes a one-turn continuity notice.
/// </summary>
public sealed record WarmSession(SessionId SessionId, string RestartNotice)
    : IWithSessionId, INoSerializationVerificationNeeded;

/// <summary>
/// The durable work that a graceful stop can resume before its fixed deadline.
/// </summary>
public sealed record RestartResumeCandidate(
    SessionId SessionId,
    string[] OriginalInputIds,
    string[] QueuedInputIds,
    long DeadlineMs) : INoSerializationVerificationNeeded;

/// <summary>
/// Requests a candidate only when its output subscriber is ready.
/// </summary>
public sealed record ResumeInterruptedSession(
    SessionId SessionId,
    RestartResumeCandidate Candidate) : IWithSessionId, INoSerializationVerificationNeeded;

public sealed record GetRestartResumeRoute(
    SessionId SessionId,
    RestartResumeCandidate Candidate) : IWithSessionId, INoSerializationVerificationNeeded;

public sealed record RestartResumeRouteResult(
    SessionId SessionId,
    IReadOnlyList<TurnContextRecord> Contexts,
    ChannelReplyRoute? ReplyRoute,
    string? BlockedReason) : INoSerializationVerificationNeeded;

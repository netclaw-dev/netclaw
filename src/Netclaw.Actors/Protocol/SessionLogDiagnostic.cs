// -----------------------------------------------------------------------
// <copyright file="SessionLogDiagnostic.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Tools;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Pre-formatted diagnostic line published explicitly into a session's
/// <c>session.log</c> via the <c>SessionLogDispatcher</c>. The producer names the
/// owning <see cref="SessionId"/> on the message itself, so the dispatcher routes
/// the line by message field rather than by ambient context (which would not flow
/// across actor mailboxes) or by inferring intent from log metadata at the sink.
/// </summary>
/// <param name="SessionId">The owning parent session.</param>
/// <param name="Line">The pre-formatted diagnostic line.</param>
/// <param name="SubSessionId">The optional child run whose log receives the line.</param>
public sealed record SessionLogDiagnostic(
    SessionId SessionId,
    string Line,
    SubAgentScopeId? SubSessionId = null)
    : IWithSessionId, INoSerializationVerificationNeeded;

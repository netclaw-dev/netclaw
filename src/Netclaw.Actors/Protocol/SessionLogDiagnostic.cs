// -----------------------------------------------------------------------
// <copyright file="SessionLogDiagnostic.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Pre-formatted diagnostic line carried from the MEL logger provider to the
/// <c>SessionLogDispatcher</c>. The provider reads the session id off each log
/// event's structured state and embeds it here so the dispatcher routes the line
/// by message field, not by any ambient context that would not flow across actor
/// mailboxes.
/// </summary>
public sealed record SessionLogDiagnostic(SessionId SessionId, string Line)
    : IWithSessionId, INoSerializationVerificationNeeded;

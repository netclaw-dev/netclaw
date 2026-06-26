// -----------------------------------------------------------------------
// <copyright file="NetclawLogProperties.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Shared structured-logging attribute keys used across the actor system,
/// channel adapters, and chat-client decorators. Centralising these constants
/// ensures that every log producer emits the same filterable field name so log
/// aggregators (Seq, OTLP) can correlate entries across subsystem boundaries.
/// </summary>
public static class NetclawLogProperties
{
    /// <summary>
    /// Structured-logging attribute key correlating a log line to a session
    /// ({channelId}/{threadTs}). Used as the WithContext / BeginScope key so
    /// actor logs and chat-client decorator logs share one filterable field in
    /// Seq/OTLP.
    /// </summary>
    public const string SessionId = "SessionId";

    /// <summary>
    /// Structured-logging attribute key correlating a log line to a specific
    /// sub-agent run within its parent session. Used alongside
    /// <see cref="SessionId"/> so sub-agent and parent logs share a parent
    /// filter while remaining independently queryable by run.
    /// </summary>
    public const string SubSessionId = "SubSessionId";
}

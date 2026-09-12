// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnBreadcrumbs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Centralizes the sub-agent spawn-lifecycle log lines. Each is an ordinary structured log call
/// wrapped in a <c>SessionId</c> scope, so the file-logger partitions it into the spawning
/// session's <c>session.log</c> (and the OTLP exporter sees the session id as an attribute).
/// <see cref="SubAgentSpawner"/> and <see cref="SpawnAgentTool"/> are plain classes — no actor
/// <c>WithContext</c> — so the scope is what carries the id. The <paramref name="logger"/> is
/// nullable so tool call sites with an optional logger can share these emitters.
/// </summary>
internal static class SubAgentSpawnBreadcrumbs
{
    public static void SpawnRequested(ILogger? logger, ToolInvocationContext context, string agentName, int taskChars)
    {
        using (BeginSessionScope(logger, context.SessionId))
            logger?.LogInformation(
                "SubAgent [{AgentName}] spawn requested (taskChars={TaskChars})",
                agentName, taskChars);
    }

    public static void NoSessionContext(ILogger? logger, ToolInvocationContext context, string agentName)
    {
        using (BeginSessionScope(logger, context.SessionId))
            logger?.LogWarning(
                "SubAgent [{AgentName}] cannot spawn — no session context available",
                agentName);
    }

    public static void NoToolsAvailable(ILogger? logger, ToolInvocationContext context, string agentName)
    {
        using (BeginSessionScope(logger, context.SessionId))
            logger?.LogWarning(
                "SubAgent [{AgentName}] has no tools available under the parent audience policy — cannot spawn",
                agentName);
    }

    public static void ChildSpawnFailed(ILogger? logger, ToolInvocationContext context, string agentName, SubAgentRunId runId, Exception ex)
    {
        using (BeginSessionScope(logger, context.SessionId))
            logger?.LogError(
                ex, "SubAgent [{AgentName}] failed to spawn child actor (runId={RunId})",
                agentName, runId.Value);
    }

    public static void ChildSpawned(ILogger? logger, ToolInvocationContext context, string agentName, SubAgentRunId runId)
    {
        using (BeginSessionScope(logger, context.SessionId))
            logger?.LogInformation(
                "SubAgent [{AgentName}] child actor spawned (runId={RunId}); dispatching RunSubAgent",
                agentName, runId.Value);
    }

    public static void Completed(ILogger? logger, ToolInvocationContext context, string agentName, SubAgentRunId runId, bool success, long durationMs)
    {
        using (BeginSessionScope(logger, context.SessionId))
            logger?.LogInformation(
                "SubAgent [{AgentName}] completed (runId={RunId}, success={Success}, duration={Duration}ms)",
                agentName, runId.Value, success, durationMs);
    }

    public static void RunFailed(ILogger? logger, ToolInvocationContext context, string agentName, SubAgentRunId runId, Exception ex)
    {
        using (BeginSessionScope(logger, context.SessionId))
            logger?.LogError(
                ex, "SubAgent [{AgentName}] run failed (runId={RunId})",
                agentName, runId.Value);
    }

    public static void SpawnRefused(ILogger? logger, ToolInvocationContext context, string agentName, TrustAudience audience, bool subsystemEnabled)
    {
        using (BeginSessionScope(logger, context.SessionId))
            logger?.LogWarning(
                "spawn_agent refused (agent={Agent}, audience={Audience}, subsystemEnabled={Enabled})",
                agentName, audience, subsystemEnabled);
    }

    public static void UnknownAgentRefused(ILogger? logger, ToolInvocationContext context, string agentName, int availableCount)
    {
        using (BeginSessionScope(logger, context.SessionId))
            logger?.LogWarning(
                "spawn_agent refused: agent '{Agent}' not found or not user-facing (availableCount={Count})",
                agentName, availableCount);
    }

    // Opens the SessionId scope the file-logger routes on. No-op (null using) when there is no
    // logger or no session — the line then falls through to daemon.log as a sessionless line.
    private static IDisposable? BeginSessionScope(ILogger? logger, string? sessionId) =>
        logger is not null && !string.IsNullOrWhiteSpace(sessionId)
            ? logger.BeginScope(new[] { new KeyValuePair<string, object>(NetclawLogProperties.SessionId, sessionId) })
            : null;
}

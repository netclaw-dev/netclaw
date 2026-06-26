// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnBreadcrumbs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Single emit point for the sub-agent spawn lifecycle. Each method fans one event
/// out to BOTH sinks from one source of truth, so callers state the event once and
/// the two renderings cannot drift:
/// <list type="bullet">
/// <item><c>daemon.log</c> / Seq — the structured diagnostic line (queryable
/// <c>{AgentName}</c>/<c>{RunId}</c>/<c>{SessionId}</c> fields, plus the exception
/// object on failure paths).</item>
/// <item><c>session.log</c> — the flat audit line for the parent's transcript, via
/// <see cref="ToolExecutionContext.EmitSessionLogLine"/>. The <c>session=…</c> suffix
/// is omitted because that file is already per-session.</item>
/// </list>
/// These are two different artifacts (operational diagnostics vs the per-session
/// audit transcript), not two ways of writing the same file — unifying them at the
/// sink was tried and reverted (routing by <c>SessionId</c> floods the transcript).
/// The <paramref name="logger"/> is nullable so tool call sites with an optional
/// logger can share these emitters.
/// </summary>
internal static class SubAgentSpawnBreadcrumbs
{
    public static void SpawnRequested(ILogger? logger, ToolExecutionContext context, string agentName, int taskChars)
    {
        logger?.LogInformation(
            "SubAgent [{AgentName}] spawn requested (taskChars={TaskChars}, session={SessionId})",
            agentName, taskChars, context.SessionId);
        context.EmitSessionLogLine?.Invoke(
            $"SubAgent [{agentName}] spawn requested (taskChars={taskChars})");
    }

    public static void NoSessionContext(ILogger? logger, ToolExecutionContext context, string agentName)
    {
        logger?.LogWarning(
            "SubAgent [{AgentName}] cannot spawn — no session context available (session={SessionId})",
            agentName, context.SessionId);
        context.EmitSessionLogLine?.Invoke(
            $"SubAgent [{agentName}] cannot spawn — no session context available");
    }

    public static void NoToolsAvailable(ILogger? logger, ToolExecutionContext context, string agentName)
    {
        logger?.LogWarning(
            "SubAgent [{AgentName}] has no tools available under the parent audience policy — cannot spawn (session={SessionId})",
            agentName, context.SessionId);
        context.EmitSessionLogLine?.Invoke(
            $"SubAgent [{agentName}] has no tools available under the parent audience policy — cannot spawn");
    }

    public static void ChildSpawnFailed(ILogger? logger, ToolExecutionContext context, string agentName, string runId, Exception ex)
    {
        logger?.LogError(
            ex, "SubAgent [{AgentName}] failed to spawn child actor (runId={RunId}, session={SessionId})",
            agentName, runId, context.SessionId);
        context.EmitSessionLogLine?.Invoke(
            $"SubAgent [{agentName}] failed to spawn child actor (runId={runId}): {ex.Message}");
    }

    public static void ChildSpawned(ILogger? logger, ToolExecutionContext context, string agentName, string runId)
    {
        logger?.LogInformation(
            "SubAgent [{AgentName}] child actor spawned (runId={RunId}, session={SessionId}); dispatching RunSubAgent",
            agentName, runId, context.SessionId);
        context.EmitSessionLogLine?.Invoke(
            $"SubAgent [{agentName}] child actor spawned (runId={runId}); dispatching RunSubAgent");
    }

    public static void Completed(ILogger? logger, ToolExecutionContext context, string agentName, string runId, bool success, long durationMs)
    {
        logger?.LogInformation(
            "SubAgent [{AgentName}] completed (runId={RunId}, success={Success}, duration={Duration}ms, session={SessionId})",
            agentName, runId, success, durationMs, context.SessionId);
        context.EmitSessionLogLine?.Invoke(
            $"SubAgent [{agentName}] completed (runId={runId}, success={success}, duration={durationMs}ms)");
    }

    public static void RunFailed(ILogger? logger, ToolExecutionContext context, string agentName, string runId, Exception ex)
    {
        logger?.LogError(
            ex, "SubAgent [{AgentName}] run failed (runId={RunId}, session={SessionId})",
            agentName, runId, context.SessionId);
        context.EmitSessionLogLine?.Invoke(
            $"SubAgent [{agentName}] run failed (runId={runId}): {ex.Message}");
    }

    public static void SpawnRefused(ILogger? logger, ToolExecutionContext context, string agentName, TrustAudience audience, bool subsystemEnabled)
    {
        logger?.LogWarning(
            "spawn_agent refused (agent={Agent}, audience={Audience}, subsystemEnabled={Enabled}, session={SessionId})",
            agentName, audience, subsystemEnabled, context.SessionId);
        context.EmitSessionLogLine?.Invoke(
            $"spawn_agent refused (agent={agentName}, audience={audience}, subsystemEnabled={subsystemEnabled})");
    }

    public static void UnknownAgentRefused(ILogger? logger, ToolExecutionContext context, string agentName, int availableCount)
    {
        logger?.LogWarning(
            "spawn_agent refused: agent '{Agent}' not found or not user-facing (availableCount={Count}, session={SessionId})",
            agentName, availableCount, context.SessionId);
        context.EmitSessionLogLine?.Invoke(
            $"spawn_agent refused: agent '{agentName}' not found or not user-facing (availableCount={availableCount})");
    }
}

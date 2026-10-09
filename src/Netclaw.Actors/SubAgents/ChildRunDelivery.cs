// -----------------------------------------------------------------------
// <copyright file="ChildRunDelivery.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Actors.SubAgents;

/// <summary>Materializes recorded child data without another task dispatch or authorization receipt.</summary>
internal static class ChildRunDelivery
{
    internal static string ToolName(BackgroundChildRun run)
        => run.StartKey is ChildRunStartKey.Slash ? "skill_load" : run.SourceOperation;

    internal static string Body(BackgroundChildRun run)
    {
        var terminal = run.PreparedTerminal ?? throw new InvalidDataException("A child result is not ready for delivery.");
        return TerminalBody(run, terminal);
    }

    internal static string TerminalBody(BackgroundChildRun run, ChildRunTerminal terminal)
    {
        var result = terminal.Result;
        return JsonSerializer.Serialize(new
        {
            run_id = run.RunId.Value, scope_id = run.ScopeId.Value, state = run.State.ToString(),
            source_operation = run.SourceOperation, outcome = result.Outcome.ToString(),
            reason = result.OutcomeReason?.Value, output = result.Output,
            working_context = result.WorkingContext, findings = result.Findings,
            log_path = result.LogPath, artifact_directory = result.ArtifactDirectory,
            warning = terminal.EvidenceWarning, checkpoint = run.ChildCheckpoint
        });
    }

    internal static SerializableChatMessage Call(BackgroundChildRun run, ToolCallId callId) => new()
    {
        Role = ChatRole.Assistant,
        ToolCalls = [new SerializableToolCall
        {
            CallId = callId, Name = new ToolName(ToolName(run)),
            ArgumentsJson = JsonSerializer.Serialize(new { run_id = run.RunId.Value, source_operation = run.SourceOperation })
        }]
    };
}

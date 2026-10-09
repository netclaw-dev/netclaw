// -----------------------------------------------------------------------
// <copyright file="CheckAgentRunTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text.Json;
using Netclaw.Tools;

namespace Netclaw.Actors.SubAgents;

[NetclawTool("check_agent_run",
    "Read an accepted child run and its authorized log and artifact paths, or request cancellation. " +
    "Cancellation admission and dispatch closure are separate facts.",
    Grant = "builtin")]
public sealed partial class CheckAgentRunTool : NetclawTool<CheckAgentRunTool.Params>
{
    public const string ToolName = "check_agent_run";

    public record Params(
        [property: Description("The run_id from an accepted child start.")] string RunId,
        [property: Description("Set true to request cancellation.")] bool Cancel = false);

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.RunId))
        {
            context.Outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput));
            return "Error: run_id is required.";
        }
        var owner = context.SpawnChildActor
            ?? throw new InvalidOperationException("Child control requires the session owner adapter.");
        var response = await owner(new ChildControlRequest(new SubAgentRunId(args.RunId), args.Cancel), ToolName, ct);
        if (response is not ChildControlReply reply)
            throw new InvalidDataException("The child control adapter returned an invalid response.");
        if (reply.Run is not { } run)
        {
            context.Outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput));
            return "Error: the child run was not found or is not accessible from this session.";
        }
        var parentStorage = context.SessionStorage
            ?? throw new InvalidOperationException("Child status requires resolved session storage.");
        var storage = parentStorage.ForChild(run.RunId, run.ScopeId);
        context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
        return JsonSerializer.Serialize(new
        {
            run_id = run.RunId.Value, scope_id = run.ScopeId.Value, agent = run.AgentName.Value,
            log_path = storage.LogPath.Value, artifact_directory = storage.ArtifactDirectory.Value,
            state = run.State.ToString(), cancellation_requested = run.CancellationRequestedAtMs is not null,
            dispatch_closed = run.DispatchClosedAtMs is not null,
            checkpoint = run.ChildCheckpoint,
            terminal = run.Terminal is null ? null : ChildRunDelivery.TerminalBody(run, run.PreparedTerminal ?? run.Terminal)
        });
    }
}

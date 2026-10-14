// -----------------------------------------------------------------------
// <copyright file="CheckAgentRunTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Tools;

namespace Netclaw.Actors.SubAgents;

[NetclawTool("check_agent_run", "Read an owned child run status, or request cancellation. Cancellation acknowledgement is not completion.", Grant = "builtin")]
public sealed partial class CheckAgentRunTool : NetclawTool<CheckAgentRunTool.Params>
{
    public sealed record Params(
        [property: Description("The run_id returned by the accepted child start.")] string RunId,
        [property: Description("Request cancellation. A later terminal result reports its outcome.")] bool Cancel = false);

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        var owner = context.SpawnChildActor
            ?? throw new InvalidOperationException("Child run status requires an owning session.");
        return (string)await owner(new ChildRunControlRequest(new SubAgentRunId(args.RunId), args.Cancel), "child-status", ct);
    }
}

// -----------------------------------------------------------------------
// <copyright file="GateAdapter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Reads the production decision of a revision whose executor returns a
/// <c>ToolAuthorizationResult</c> (dev up to authorization PR 6c). The method is
/// private on some revisions, so the adapter finds it by name.
/// </summary>
internal static class CorpusProbeAdapter
{
    public const string Name = "gate";

    private static readonly MethodInfo Evaluate = typeof(DispatchingToolExecutor).GetMethod(
            "EvaluateAuthorizationResultAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("This revision has no EvaluateAuthorizationResultAsync. Use the authorizer adapter.");

    public static async Task<CorpusDecisionFields> DecideAsync(
        DispatchingToolExecutor executor,
        FunctionCallContent call,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var result = await (Task<ToolAuthorizationResult>)Evaluate.Invoke(executor, [call, context, ct])!;
        var decision = result.Decision;
        var execution = result switch
        {
            ToolAuthorizationResult.ShellExecution shell => $"shell-execution:{shell.Analysis.Source}@{shell.Analysis.WorkingDirectory}",
            ToolAuthorizationResult.ShellValidation => "shell-validation",
            ToolAuthorizationResult.DirectExecution => "direct",
            ToolAuthorizationResult.Stopped => "stopped",
            _ => throw new ArgumentOutOfRangeException(nameof(call), result, "Unknown authorization result.")
        };
        return new CorpusDecisionFields(
            decision.Outcome.ToString(),
            decision.AllowReason?.ToString(),
            decision.DenyReason,
            decision.DenyMessage,
            decision.AgentCorrections?.Items,
            decision.ApprovalContext,
            decision.ApprovalMatches,
            decision.ShellPolicyTrace,
            execution);
    }
}

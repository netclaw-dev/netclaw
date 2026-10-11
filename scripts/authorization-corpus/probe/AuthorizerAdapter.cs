// -----------------------------------------------------------------------
// <copyright file="AuthorizerAdapter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Tools;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Reads the decision of <c>ToolAuthorizer</c> (authorization PR 6a and later).
/// The case names map to the outcome names of the older decision type, so the
/// two adapters give the same text for the same decision.
/// </summary>
internal static class CorpusProbeAdapter
{
    public const string Name = "authorizer";

    public static async Task<CorpusDecisionFields> DecideAsync(
        DispatchingToolExecutor executor,
        FunctionCallContent call,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var decision = await executor.Authorizer.AuthorizeAsync(call, context, ct);
        var isShell = string.Equals(call.Name, ShellTool.ToolName, StringComparison.Ordinal);
        return decision switch
        {
            AuthorizationDecision.Allowed allowed => new CorpusDecisionFields(
                "Allowed", allowed.Reason.ToString(), null, null, null, null, allowed.Matches, allowed.Trace,
                (isShell, allowed.Analysis) switch
                {
                    (true, { } analysis) => $"shell-execution:{analysis.Source}@{analysis.WorkingDirectory}",
                    (true, null) => "shell-validation",
                    (false, null) => "direct",
                    (false, not null) => throw new InvalidOperationException("Only a shell call can carry an analysis.")
                }),
            AuthorizationDecision.NeedsConsent consent => new CorpusDecisionFields(
                "RequiresApproval", null, null, null, null, consent.Request, consent.Matches, consent.Trace, "stopped"),
            AuthorizationDecision.CorrectionRequired correction => new CorpusDecisionFields(
                "RequiresAgentCorrection", null, null, null, correction.Corrections.Items, null, correction.Matches,
                correction.Trace, "stopped"),
            AuthorizationDecision.Denied denied => new CorpusDecisionFields(
                "Denied", null, denied.Reason, denied.Message, null, null, denied.Matches, denied.Trace, "stopped"),
            _ => throw new ArgumentOutOfRangeException(nameof(call), decision, "Unknown authorization decision.")
        };
    }
}

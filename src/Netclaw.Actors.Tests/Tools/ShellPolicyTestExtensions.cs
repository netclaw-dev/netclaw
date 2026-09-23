// -----------------------------------------------------------------------
// <copyright file="ShellPolicyTestExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Tests.Tools;

internal static class ShellPolicyTestExtensions
{
    internal static ToolAuthorizationDecision GetShellPreflightDecision(
        this ToolAccessPolicy policy,
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments = null)
        => policy.AuthorizeShellPreflight(tool, context, arguments).GetDecision();

    internal static ToolAuthorizationDecision GetDecision(this ShellPolicyPreflightResult result)
        => result switch
        {
            ShellPolicyPreflightResult.Complete complete => complete.Result.Decision,
            ShellPolicyPreflightResult.Continue continuation =>
                ToolAuthorizationDecision.RequiresApproval(continuation.ApprovalContext),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
}

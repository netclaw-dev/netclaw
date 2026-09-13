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
        => policy.AuthorizeShellPreflight(tool, context, arguments).Decision;
}

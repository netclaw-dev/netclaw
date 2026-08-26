// -----------------------------------------------------------------------
// <copyright file="NativeToolShellCorrectionDetector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal static class NativeToolShellCorrectionDetector
{
    internal static ToolAgentCorrection.NativeToolSuggested? Detect(
        ShellCommandAnalysis analysis,
        ToolRegistry registry,
        ToolAccessPolicy policy,
        ToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        if (!analysis.IsResolved || analysis.HasDynamicSyntax)
            return null;

        foreach (var occurrence in analysis.Commands)
        {
            var verb = occurrence.Clause.Verb;
            if (!occurrence.IsComplete
                || verb.IsDynamic
                || verb.Tokens.Count == 0)
            {
                continue;
            }

            var authoredName = verb.Tokens[0];
            var registration = registry.GetRegistrationByToolName(authoredName);
            if (registration is null
                || registration.Tool is McpToolAdapter
                || !string.Equals(registration.Tool.Name, authoredName, StringComparison.Ordinal)
                || string.Equals(authoredName, ShellTool.ToolName, StringComparison.Ordinal)
                || !policy.IsToolExposed(registration.Tool, context))
            {
                continue;
            }

            return new ToolAgentCorrection.NativeToolSuggested(new ToolName(registration.Tool.Name));
        }

        return null;
    }
}

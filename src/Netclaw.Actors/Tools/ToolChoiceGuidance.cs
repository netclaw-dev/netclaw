// -----------------------------------------------------------------------
// <copyright file="ToolChoiceGuidance.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Actors.Tools;

internal static class ToolChoiceGuidance
{
    public const string DirectorySelectionOrder = """
        Choose directories in this order:
        1. For declared-project work, omit WorkingDirectory; the shell uses project_dir.
        2. For one call in a named child directory, set typed WorkingDirectory.
        3. Use session_dir only for disposable work outside a project.
        4. Use an inline directory change only when the task requests that behavior.
        """;

    public const string ShellCompositionOrder = """
        Keep shell approval friction bounded:
        1. Start with the smallest single shell operation that directly answers the request.
        2. Add diagnostics only when the task requires them.
        3. After an approval-required result, do not split or retry shell variants.
        4. Use an available structured tool when it can finish; otherwise report the blocked operation once.
        """;
}

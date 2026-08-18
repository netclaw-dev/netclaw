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
        2. Do not use shell only to verify a successful structured tool result.
        3. After an approval-required result, do not retry or substitute shell variants.
        4. A `Tool access denied:` result is terminal; do not change scope, retry, or substitute another tool.
        5. Apply one `Tool execution deferred:` correction unchanged; otherwise use a structured tool or report the block once.
        """;
}

// -----------------------------------------------------------------------
// <copyright file="ToolChoiceGuidance.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Actors.Tools;

internal static class ToolChoiceGuidance
{
    public const string StructuredWorkspaceSelection = """
        Prefer structured workspace tools:
        1. Use file_search for bounded recursive name or literal text search.
        2. Use file_read for bounded file content and image metadata.
        3. Issue independent file_read calls in parallel when several paths are known.
        4. Use tool_output_read to continue a spilled result by call id.
        5. Use load_tool directly when the exact specialty tool name is known.
        6. Use search_tools when the capability is known but its exact tool name is not.
        7. Use shell or an interpreter only when no structured tool expresses the operation.
        """;

    public const string DirectorySelectionOrder = """
        Choose directories in this order:
        1. For declared-project work, omit WorkingDirectory; the shell uses project_dir.
        2. For one call in a named child directory, set typed WorkingDirectory.
        3. Use session_dir for disposable writable work outside a project; do not substitute platform temporary storage.
        4. Use an inline directory change only when the task requests that behavior.
        """;

    public const string ShellCompositionOrder = """
        Keep shell approval friction bounded:
        1. Start with the smallest single shell operation that directly answers the request.
        2. Use one operation per call. Add a pipeline only when the requested result requires it.
        3. Do not use shell only to verify a successful structured tool result.
        4. After an approval-required result, do not retry or substitute shell variants.
        5. A `Tool access denied:` result is terminal; do not change scope, retry, or substitute another tool.
        6. Apply one `Tool execution deferred:` correction unchanged; otherwise use a structured tool or report the block once.
        """;
}

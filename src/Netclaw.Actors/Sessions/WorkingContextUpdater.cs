using System.Text.Json;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Extracts file paths from tool call arguments and updates
/// <see cref="WorkingContext.RecentFiles"/>. Lives outside
/// <see cref="LlmSessionActor"/> so the path-extraction logic is
/// testable in isolation.
///
/// There is no tool-name allowlist. Any tool whose <c>ArgumentsJson</c>
/// contains a recognizable file-path field (see
/// <see cref="TryExtractFilePath"/>) has its path tracked. This lets
/// first-party tools, MCP filesystem tools, and any future path-taking
/// tool participate without a central registry.
/// </summary>
internal static class WorkingContextUpdater
{
    // First-party Netclaw tools (FileReadTool, FileWriteTool, FileEditTool)
    // use PascalCase parameter names via C# records, which NetclawToolGenerator
    // emits into the tool JSON schema verbatim — so real arguments come in
    // keyed as `{"Path": "..."}`. MCP tools and other conventions use the
    // camelCase/snake_case variants. Probe all common forms.
    private static readonly string[] PathFieldNames =
    {
        "Path",
        "path",
        "FilePath",
        "file_path",
        "filePath",
        "File",
        "file",
        "FileName",
        "filename",
        "fileName",
    };

    /// <summary>
    /// Process a batch of tool results that just completed, find each
    /// result's matching tool call in history, extract any file path
    /// argument, and return the updated <see cref="WorkingContext"/>.
    /// Pure — does not mutate inputs. Returns the same instance when no
    /// result produced a path change.
    /// </summary>
    public static WorkingContext UpdateFromToolResults(
        WorkingContext current,
        IReadOnlyList<SerializableChatMessage> history,
        IReadOnlyList<SerializableChatMessage> results)
    {
        if (results.Count == 0)
            return current;

        // Build a lookup from CallId → ArgumentsJson by scanning backward
        // from the end of history for Assistant messages with tool calls.
        // Typical turns have one producing assistant message immediately
        // before the tool-result batch, so the walk exits after a single
        // match + collecting its calls. In pathological cases where the
        // batch mixes calls from multiple assistants, we keep walking
        // until every result's CallId is accounted for.
        var pendingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (!string.IsNullOrEmpty(r.ToolCallId))
                pendingIds.Add(r.ToolCallId);
        }
        if (pendingIds.Count == 0)
            return current;

        var argsByCallId = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = history.Count - 1; i >= 0 && pendingIds.Count > 0; i--)
        {
            var msg = history[i];
            if (msg.Role != ChatRole.Assistant || msg.ToolCalls.Count == 0)
                continue;

            foreach (var tc in msg.ToolCalls)
            {
                if (pendingIds.Remove(tc.CallId))
                    argsByCallId[tc.CallId] = tc.ArgumentsJson;
            }
        }

        var updated = current;
        foreach (var result in results)
        {
            if (result.ToolCallId is null
                || !argsByCallId.TryGetValue(result.ToolCallId, out var argumentsJson))
            {
                continue;
            }

            if (TryExtractFilePath(argumentsJson, out var path))
                updated = updated.AddRecentFile(path);
        }
        return updated;
    }

    /// <summary>
    /// Parse a tool call's <c>ArgumentsJson</c> and extract a file path
    /// from one of the well-known field names. Returns false when the
    /// arguments are empty, unparseable, or do not contain a recognized
    /// path field.
    /// </summary>
    public static bool TryExtractFilePath(string? argumentsJson, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson == "{}")
            return false;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var name in PathFieldNames)
            {
                if (doc.RootElement.TryGetProperty(name, out var prop)
                    && prop.ValueKind == JsonValueKind.String)
                {
                    var value = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        path = value;
                        return true;
                    }
                }
            }
        }
        catch (JsonException) // slopwatch-ignore: SW003 malformed LLM-generated JSON is benign — see body comment
        {
            // Malformed arguments are treated as "no path" — not worth
            // failing the actor for.
        }

        return false;
    }
}

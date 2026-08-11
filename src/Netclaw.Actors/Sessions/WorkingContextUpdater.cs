// -----------------------------------------------------------------------
// <copyright file="WorkingContextUpdater.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Event;
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
    [
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
    ];

    /// <summary>
    /// Process a batch of tool results that just completed, find each
    /// result's matching tool call in history, extract any file path
    /// argument, and return the updated <see cref="WorkingContext"/>.
    /// Pure — does not mutate inputs. Returns the same instance when no
    /// result produced a path change.
    ///
    /// Optionally takes an <see cref="ILoggingAdapter"/> so the actor can
    /// emit debug-level observability when a tool call with non-empty
    /// object arguments is processed but no recognized path field matches
    /// (signalling potential schema drift — a new tool whose argument
    /// keys aren't in <see cref="PathFieldNames"/>).
    /// </summary>
    public static WorkingContext UpdateFromToolResults(
        WorkingContext current,
        IReadOnlyList<SerializableChatMessage> history,
        IReadOnlyList<SerializableChatMessage> results,
        ILoggingAdapter? log = null)
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
            if (r.ToolCallId is { } id && !string.IsNullOrEmpty(id.Value))
                pendingIds.Add(id.Value);
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
                if (pendingIds.Remove(tc.CallId.Value))
                    argsByCallId[tc.CallId.Value] = tc.ArgumentsJson;
            }
        }

        var updated = current;
        foreach (var result in results)
        {
            if (result.ToolCallId is not { } resultCallId
                || !argsByCallId.TryGetValue(resultCallId.Value, out var argumentsJson))
            {
                continue;
            }

            if (TryExtractFilePath(argumentsJson, out var path))
            {
                var next = updated.AddRecentFile(path);
                if (!ReferenceEquals(next, updated))
                {
                    log?.Debug(
                        "WorkingContext: tracked file {Path} from tool call {CallId} ({ToolName})",
                        path, result.ToolCallId, result.Name ?? "unknown");
                }
                updated = next;
            }
            else if (HasStringValuedObjectArgs(argumentsJson))
            {
                // The arguments parsed to a non-empty JSON object with at
                // least one string-valued field, but none of our probe
                // names matched. This is the schema-drift signal — a new
                // tool whose path argument uses an unrecognized key, or
                // a provider that renamed keys in transit. Operators
                // hunting "why isn't this file showing up in
                // [working-context]" will see this in debug logs.
                log?.Debug(
                    "WorkingContext: tool call {CallId} ({ToolName}) had string-valued arguments but no recognized path field matched (probed: Path/path/FilePath/file_path/filePath/File/file/FileName/filename/fileName); possible schema drift",
                    result.ToolCallId, result.Name ?? "unknown");
            }
        }
        return updated;
    }

    /// <summary>
    /// Returns true when <paramref name="argumentsJson"/> parses to a
    /// non-empty JSON object that contains at least one string-valued
    /// property. Used to distinguish "tool has no path arg" (normal,
    /// quiet) from "tool has string args we didn't recognize" (schema
    /// drift, worth logging).
    /// </summary>
    private static bool HasStringValuedObjectArgs(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson == "{}")
            return false;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                {
                    return true;
                }
            }
        }
        catch (JsonException) // slopwatch-ignore: SW003 malformed JSON is benign — see TryExtractFilePath
        {
        }

        return false;
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

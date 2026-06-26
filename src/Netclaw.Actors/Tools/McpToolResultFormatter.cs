// -----------------------------------------------------------------------
// <copyright file="McpToolResultFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Renders the object returned by an MCP tool invocation into the string the
/// model sees.
/// <para>
/// The MCP SDK (<c>McpClientTool.InvokeCoreAsync</c>) returns clean
/// <c>AIContent</c> for a successful, non-structured result — but when the
/// server sets <c>isError: true</c> it returns the <em>entire</em>
/// <c>CallToolResult</c> serialized as a <see cref="JsonElement"/>. Passing that
/// through <c>ToString()</c> hands the model a raw JSON blob
/// (<c>{"content":[{"type":"text","text":"..."}],"isError":true}</c>), which it
/// cannot distinguish from a transport failure or a netclaw error — the exact
/// confusion behind #1495. This extracts the error's text and surfaces it as a
/// clear, attributed tool-error message instead.
/// </para>
/// </summary>
public static class McpToolResultFormatter
{
    public static string Format(object? result, string toolName)
    {
        if (result is JsonElement element && IsErrorResult(element))
        {
            var detail = ExtractText(element);
            return string.IsNullOrWhiteSpace(detail)
                ? $"Error: MCP tool '{toolName}' reported a failure (no detail provided)."
                : $"Error: MCP tool '{toolName}' reported a failure: {detail}";
        }

        return result?.ToString() ?? string.Empty;
    }

    private static bool IsErrorResult(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty("isError", out var isError)
           && isError.ValueKind == JsonValueKind.True;

    private static string ExtractText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && text.GetString() is { Length: > 0 } s)
            {
                parts.Add(s);
            }
        }

        return string.Join("\n", parts);
    }
}

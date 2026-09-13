// -----------------------------------------------------------------------
// <copyright file="McpToolResultFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Renders the object returned by an MCP tool invocation into the string the
/// model sees.
/// <para>
/// The MCP SDK (<c>McpClientTool.InvokeCoreAsync</c>) returns clean
/// <c>AIContent</c> for a successful, single-content result — but serializes the
/// <em>entire</em> <c>CallToolResult</c> to a <see cref="JsonElement"/> whenever
/// it sets <c>isError: true</c> <strong>or</strong> the result carries
/// <c>structuredContent</c>. Passing that through <c>ToString()</c> hands the
/// model a raw JSON blob
/// (<c>{"content":[{"type":"text","text":"..."}],"isError":true}</c>) it cannot
/// distinguish from a transport failure or a netclaw error — the confusion behind
/// #1495. This unwraps both shapes: errors become a clear, attributed message,
/// and structured successes surface their actual content instead of the
/// <c>isError:false</c> wrapper.
/// </para>
/// </summary>
public static class McpToolResultFormatter
{
    public static string FormatWithReceipt(
        object? result,
        string toolName,
        ToolInvocationContext context)
    {
        var text = Format(result, toolName);
        return TryGetErrorDetail(result, out _)
            ? context.TransientFailure(text)
            : text;
    }

    public static string Format(object? result, string toolName)
    {
        if (result is JsonElement element && IsCallToolResult(element))
        {
            var detail = ExtractDetail(element);

            if (IsError(element))
            {
                return string.IsNullOrWhiteSpace(detail)
                    ? $"Error: MCP tool '{toolName}' reported a failure (no detail provided)."
                    : $"Error: MCP tool '{toolName}' reported a failure: {detail}";
            }

            // Structured success: surface the clean detail rather than the
            // {content,structuredContent,isError} wrapper. Fall back to the raw
            // element only if there is genuinely nothing extractable.
            return string.IsNullOrWhiteSpace(detail) ? element.GetRawText() : detail;
        }

        return result?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Reports whether the MCP server flagged this result as a failure, and yields the
    /// server's own detail. A tool-level failure arrives as an ordinary successful
    /// response, so no exception reaches the transport layer and nothing downstream can
    /// tell it apart from a normal result without this signal.
    /// </summary>
    public static bool TryGetErrorDetail(object? result, out string detail)
    {
        detail = string.Empty;
        if (result is not JsonElement element || !IsCallToolResult(element) || !IsError(element))
            return false;

        detail = ExtractDetail(element);
        return true;
    }

    private static bool IsCallToolResult(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
           && (element.TryGetProperty("content", out _) || element.TryGetProperty("isError", out _));

    private static bool IsError(JsonElement element)
        => element.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Prefers the human-readable text blocks, then falls back to
    /// <c>structuredContent</c> so machine-readable detail (e.g. validation errors
    /// returned as a JSON object with no text block) is never dropped — which a
    /// bare <c>content[].text</c> scan would silently do.
    /// </summary>
    private static string ExtractDetail(JsonElement element)
    {
        var text = JoinTextContent(element);
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        if (element.TryGetProperty("structuredContent", out var structured)
            && structured.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            return structured.GetRawText();

        return string.Empty;
    }

    private static string JoinTextContent(JsonElement element)
    {
        if (!element.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
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

// -----------------------------------------------------------------------
// <copyright file="McpToolResultFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Renders the object returned by an MCP tool invocation into the string the
/// model sees.
/// <para>
/// The MCP SDK (<c>McpClientTool.InvokeCoreAsync</c>) returns clean
/// <c>AIContent</c> for a successful, single-content result — but serializes the
/// <em>entire</em> <c>CallToolResult</c> to a <see cref="JsonElement"/> whenever
/// it sets <c>isError: true</c> or the result carries structured content,
/// application metadata, or an unsupported content block. Passing that through
/// <c>ToString()</c> hands the
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

            return string.IsNullOrWhiteSpace(detail)
                ? $"MCP tool '{toolName}' returned no model-readable content."
                : detail;
        }

        if (result is AIContent singleContent)
            return FormatAiContent(singleContent);

        if (result is IEnumerable<AIContent> contents)
            return FormatAiContents(contents);

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
    /// Projects model-readable content blocks, then falls back to structured content.
    /// This order preserves the server's content sequence and excludes binary data.
    /// </summary>
    private static string ExtractDetail(JsonElement element)
    {
        var (content, hasText) = ProjectJsonContent(element);
        if (hasText)
            return content;

        if (element.TryGetProperty("structuredContent", out var structured)
            && structured.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            var structuredText = structured.GetRawText();
            return string.IsNullOrWhiteSpace(content)
                ? structuredText
                : $"{content}\n{structuredText}";
        }

        return content;
    }

    private static (string Content, bool HasText) ProjectJsonContent(JsonElement element)
    {
        if (!element.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return (string.Empty, false);

        var parts = new List<string>();
        var hasText = false;
        foreach (var block in content.EnumerateArray())
        {
            var (part, blockHasText) = ProjectJsonContentBlock(block);
            if (!string.IsNullOrEmpty(part))
                parts.Add(part);
            hasText |= blockHasText;
        }

        return (string.Join("\n", parts), hasText);
    }

    private static (string Content, bool HasText) ProjectJsonContentBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object
            || !block.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || typeElement.GetString() is not { Length: > 0 } type)
        {
            return ("[unsupported MCP content: unknown]", false);
        }

        return type switch
        {
            "text" when GetStringProperty(block, "text") is { } text => (text, true),
            "text" => ("[unsupported MCP content: text]", false),
            "image" => (FormatJsonDataContentMarker(block, isImage: true), false),
            "audio" => (FormatJsonDataContentMarker(block, isImage: false), false),
            "resource" => ProjectEmbeddedResource(block),
            _ => ($"[unsupported MCP content: {type}]", false),
        };
    }

    private static (string Content, bool HasText) ProjectEmbeddedResource(JsonElement block)
    {
        if (!block.TryGetProperty("resource", out var resource)
            || resource.ValueKind != JsonValueKind.Object)
        {
            return ("[unsupported MCP content: resource]", false);
        }

        if (GetStringProperty(resource, "text") is { } text)
            return (text, true);

        if (resource.TryGetProperty("blob", out _))
            return (FormatJsonDataContentMarker(resource, isImage: false), false);

        return ("[unsupported MCP content: resource]", false);
    }

    private static string FormatJsonDataContentMarker(JsonElement element, bool isImage)
    {
        var mediaType = GetStringProperty(element, "mimeType") ?? "unknown media type";
        return isImage || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? $"[image: {mediaType}]"
            : $"[attachment: {mediaType}]";
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
           && property.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static string FormatAiContents(IEnumerable<AIContent> contents)
    {
        var parts = new List<string>();
        foreach (var content in contents)
        {
            var part = FormatAiContent(content);
            if (!string.IsNullOrEmpty(part))
                parts.Add(part);
        }

        return string.Join("\n", parts);
    }

    private static string FormatAiContent(AIContent content)
        => content switch
        {
            TextContent text => text.Text,
            DataContent data => FormatDataContentMarker(data),
            _ => $"[unsupported MCP content: {content.GetType().Name}]",
        };

    private static string FormatDataContentMarker(DataContent data)
        => data.HasTopLevelMediaType("image")
            ? $"[image: {data.MediaType}]"
            : $"[attachment: {data.MediaType}]";
}

// -----------------------------------------------------------------------
// <copyright file="McpToolResultFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Contains the text and artifact candidates from one MCP result.
/// </summary>
public sealed class McpToolResultProjection
{
    internal McpToolResultProjection(
        string text,
        bool isError,
        string errorDetail,
        IEnumerable<McpResultArtifact> artifacts,
        IEnumerable<string> artifactNotes)
    {
        Text = text;
        IsError = isError;
        ErrorDetail = errorDetail;
        Artifacts = Array.AsReadOnly(artifacts.ToArray());
        ArtifactNotes = Array.AsReadOnly(artifactNotes.ToArray());
    }

    /// <summary>Gets the model-readable result text.</summary>
    public string Text { get; }

    /// <summary>Gets whether the MCP server declared a tool error.</summary>
    public bool IsError { get; }

    /// <summary>Gets the server detail for a declared tool error.</summary>
    public string ErrorDetail { get; }

    /// <summary>Gets the untrusted artifact candidates.</summary>
    public IReadOnlyList<McpResultArtifact> Artifacts { get; }

    /// <summary>Gets safe notes for candidates that the projection could not decode.</summary>
    public IReadOnlyList<string> ArtifactNotes { get; }
}

/// <summary>
/// Contains one copied MCP artifact candidate before content admission.
/// </summary>
public sealed class McpResultArtifact
{
    private readonly byte[] _data;

    internal McpResultArtifact(ReadOnlyMemory<byte> data, DeclaredMimeType declaredMimeType, string? name)
    {
        _data = data.ToArray();
        DeclaredMimeType = declaredMimeType;
        Name = name;
    }

    /// <summary>Gets a read-only view of the copied candidate bytes.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>Gets the untrusted MIME type from the MCP server.</summary>
    public DeclaredMimeType DeclaredMimeType { get; }

    /// <summary>Gets the optional untrusted name from the MCP server.</summary>
    public string? Name { get; }
}

/// <summary>
/// Projects MCP SDK result shapes into model text and artifact candidates.
/// </summary>
public static class McpToolResultFormatter
{
    public static string FormatWithReceipt(
        object? result,
        string toolName,
        ToolInvocationContext context)
        => FormatWithReceipt(ProjectCore(result, toolName, captureArtifacts: false), context);

    public static string FormatWithReceipt(
        McpToolResultProjection projection,
        ToolInvocationContext context)
        => projection.IsError
            ? context.TransientFailure(projection.Text)
            : projection.Text;

    public static string Format(object? result, string toolName)
        => ProjectCore(result, toolName, captureArtifacts: false).Text;

    /// <summary>
    /// Projects one SDK result without admitting artifact bytes into storage.
    /// </summary>
    public static McpToolResultProjection Project(object? result, string toolName)
        => ProjectCore(result, toolName, captureArtifacts: true);

    private static McpToolResultProjection ProjectCore(
        object? result,
        string toolName,
        bool captureArtifacts)
    {
        if (result is JsonElement element && IsCallToolResult(element))
            return ProjectCallToolResult(element, toolName, captureArtifacts);

        if (result is AIContent singleContent)
            return ProjectAiContents([singleContent], captureArtifacts);

        if (result is IEnumerable<AIContent> contents)
            return ProjectAiContents(contents, captureArtifacts);

        return new McpToolResultProjection(
            result?.ToString() ?? string.Empty,
            false,
            string.Empty,
            [],
            []);
    }

    /// <summary>
    /// Reports whether the MCP server flagged this result as a failure.
    /// </summary>
    public static bool TryGetErrorDetail(object? result, out string detail)
    {
        var projection = ProjectCore(result, string.Empty, captureArtifacts: false);
        detail = projection.ErrorDetail;
        return projection.IsError;
    }

    private static McpToolResultProjection ProjectCallToolResult(
        JsonElement element,
        string toolName,
        bool captureArtifacts)
    {
        var isError = IsError(element);
        var builder = new ProjectionBuilder(captureArtifacts && !isError);
        var hasText = ProjectJsonContent(element, builder);
        var detail = string.Join("\n", builder.TextParts);

        if (!hasText
            && element.TryGetProperty("structuredContent", out var structured)
            && structured.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            builder.TextParts.Add(structured.GetRawText());
            detail = string.Join("\n", builder.TextParts);
        }

        if (isError)
        {
            var text = string.IsNullOrWhiteSpace(detail)
                ? $"Error: MCP tool '{toolName}' reported a failure (no detail provided)."
                : $"Error: MCP tool '{toolName}' reported a failure: {detail}";
            return new McpToolResultProjection(text, true, detail, [], []);
        }

        var successText = string.IsNullOrWhiteSpace(detail)
            ? $"MCP tool '{toolName}' returned no model-readable content."
            : detail;
        return builder.Build(successText);
    }

    private static McpToolResultProjection ProjectAiContents(
        IEnumerable<AIContent> contents,
        bool captureArtifacts)
    {
        var builder = new ProjectionBuilder(captureArtifacts);
        foreach (var content in contents)
        {
            switch (content)
            {
                case TextContent text:
                    if (!string.IsNullOrEmpty(text.Text))
                        builder.TextParts.Add(text.Text);
                    break;
                case DataContent data:
                    builder.TextParts.Add(FormatDataContentMarker(data));
                    ProjectDataContent(data, builder);
                    break;
                default:
                    builder.TextParts.Add($"[unsupported MCP content: {content.GetType().Name}]");
                    break;
            }
        }

        return builder.Build(string.Join("\n", builder.TextParts));
    }

    private static bool IsCallToolResult(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
           && (element.TryGetProperty("content", out _) || element.TryGetProperty("isError", out _));

    private static bool IsError(JsonElement element)
        => element.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Projects readable blocks in source order. The method excludes binary fields from text.
    /// </summary>
    private static bool ProjectJsonContent(JsonElement element, ProjectionBuilder builder)
    {
        if (!element.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return false;

        var hasText = false;
        foreach (var block in content.EnumerateArray())
            hasText |= ProjectJsonContentBlock(block, builder);

        return hasText;
    }

    private static bool ProjectJsonContentBlock(JsonElement block, ProjectionBuilder builder)
    {
        if (block.ValueKind != JsonValueKind.Object
            || !block.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || typeElement.GetString() is not { Length: > 0 } type)
        {
            builder.TextParts.Add("[unsupported MCP content: unknown]");
            return false;
        }

        switch (type)
        {
            case "text" when GetStringProperty(block, "text") is { } text:
                builder.TextParts.Add(text);
                return true;
            case "text":
                builder.TextParts.Add("[unsupported MCP content: text]");
                return false;
            case "image":
                builder.TextParts.Add(FormatJsonDataContentMarker(block, true));
                ProjectJsonArtifact(block, "data", builder);
                return false;
            case "audio":
                builder.TextParts.Add(FormatJsonDataContentMarker(block, false));
                ProjectJsonArtifact(block, "data", builder);
                return false;
            case "resource":
                return ProjectEmbeddedResource(block, builder);
            default:
                builder.TextParts.Add($"[unsupported MCP content: {type}]");
                return false;
        }
    }

    private static bool ProjectEmbeddedResource(JsonElement block, ProjectionBuilder builder)
    {
        if (!block.TryGetProperty("resource", out var resource)
            || resource.ValueKind != JsonValueKind.Object)
        {
            builder.TextParts.Add("[unsupported MCP content: resource]");
            return false;
        }

        if (GetStringProperty(resource, "text") is { } text)
        {
            builder.TextParts.Add(text);
            return true;
        }

        if (resource.TryGetProperty("blob", out _))
        {
            builder.TextParts.Add(FormatJsonDataContentMarker(resource, false));
            ProjectJsonArtifact(resource, "blob", builder);
            return false;
        }

        builder.TextParts.Add("[unsupported MCP content: resource]");
        return false;
    }

    private static void ProjectJsonArtifact(JsonElement element, string dataProperty, ProjectionBuilder builder)
    {
        if (!builder.CaptureArtifacts)
            return;

        var encoded = GetStringProperty(element, dataProperty);
        var mediaType = new DeclaredMimeType(GetStringProperty(element, "mimeType"));
        var decodedLength = encoded is not null && TryGetDecodedBase64Length(encoded, out var length)
            ? length
            : 0;
        if (!builder.TryReserveArtifact(decodedLength))
            return;

        if (encoded is null)
        {
            builder.ArtifactNotes.Add("[MCP artifact rejected: the result did not contain artifact bytes.]");
            return;
        }

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            builder.AddReservedArtifact(bytes, mediaType, GetArtifactName(element));
        }
        catch (FormatException)
        {
            builder.ArtifactNotes.Add("[MCP artifact rejected: the result contained invalid Base64 data.]");
        }
    }

    private static void ProjectDataContent(DataContent data, ProjectionBuilder builder)
        => builder.AddArtifact(
            data.Data,
            new DeclaredMimeType(data.MediaType),
            data.Name);

    private static bool TryGetDecodedBase64Length(string encoded, out int decodedLength)
    {
        var usefulCharacters = 0;
        var last = '\0';
        var secondLast = '\0';
        foreach (var character in encoded)
        {
            if (char.IsWhiteSpace(character))
                continue;

            usefulCharacters++;
            secondLast = last;
            last = character;
        }

        if (usefulCharacters == 0 || usefulCharacters % 4 != 0)
        {
            decodedLength = 0;
            return false;
        }

        var padding = last == '=' ? 1 : 0;
        if (secondLast == '=')
            padding++;
        decodedLength = (usefulCharacters / 4 * 3) - padding;
        return true;
    }

    private static string? GetArtifactName(JsonElement element)
    {
        if (GetStringProperty(element, "name") is { } name)
            return name;

        if (GetStringProperty(element, "uri") is not { } uriText
            || !Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        return Path.GetFileName(path);
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

    private static string FormatDataContentMarker(DataContent data)
        => data.HasTopLevelMediaType("image")
            ? $"[image: {data.MediaType}]"
            : $"[attachment: {data.MediaType}]";

    private sealed class ProjectionBuilder(bool captureArtifacts = true)
    {
        private int _candidateCount;
        private long _artifactBytes;
        private bool _countLimitReported;

        internal bool CaptureArtifacts { get; } = captureArtifacts;
        internal List<string> TextParts { get; } = [];
        internal List<McpResultArtifact> Artifacts { get; } = [];
        internal List<string> ArtifactNotes { get; } = [];

        internal bool TryReserveArtifact(int byteCount)
        {
            _candidateCount++;
            if (_candidateCount > ChannelAttachmentPolicy.DefaultMaxFilesPerMessage)
            {
                if (!_countLimitReported)
                {
                    ArtifactNotes.Add(
                        $"[MCP artifacts limited: Netclaw accepted only the first {ChannelAttachmentPolicy.DefaultMaxFilesPerMessage} candidates.]");
                    _countLimitReported = true;
                }

                return false;
            }

            if (byteCount > ContentPolicy.DefaultMaxFileSizeBytes - _artifactBytes)
            {
                ArtifactNotes.Add(
                    $"[MCP artifact {_candidateCount} rejected: the artifact byte limit was exceeded.]");
                return false;
            }

            _artifactBytes += byteCount;
            return true;
        }

        internal void AddArtifact(
            ReadOnlyMemory<byte> data,
            DeclaredMimeType declaredMimeType,
            string? name)
        {
            if (!CaptureArtifacts)
                return;

            if (TryReserveArtifact(data.Length))
                AddReservedArtifact(data, declaredMimeType, name);
        }

        internal void AddReservedArtifact(
            ReadOnlyMemory<byte> data,
            DeclaredMimeType declaredMimeType,
            string? name)
            => Artifacts.Add(new McpResultArtifact(data, declaredMimeType, name));

        internal McpToolResultProjection Build(string text)
            => new(text, false, string.Empty, Artifacts, ArtifactNotes);
    }
}

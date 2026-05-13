// -----------------------------------------------------------------------
// <copyright file="ChatMessageConverter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Converts between persistence-safe <see cref="SerializableChatMessage"/> and
/// MEAI <see cref="AiChatMessage"/> types. Boundary conversion only — called
/// when preparing LLM requests and processing LLM responses.
/// </summary>
public static class ChatMessageConverter
{
    public static AiChatMessage ToAiMessage(SerializableChatMessage msg, string? sessionDir = null, ILogger? logger = null)
    {
        var role = msg.Role switch
        {
            ChatRole.User => AiChatRole.User,
            ChatRole.Assistant => AiChatRole.Assistant,
            ChatRole.System => AiChatRole.System,
            ChatRole.Tool => AiChatRole.Tool,
            _ => AiChatRole.User
        };

        // Tool result message: wrap content in FunctionResultContent
        if (msg.Role == ChatRole.Tool && msg.ToolCallId is not null)
        {
            var resultContent = new FunctionResultContent(msg.ToolCallId, msg.Content);
            return new AiChatMessage(role, [resultContent]);
        }

        // Assistant message with tool calls: reconstruct FunctionCallContent items
        if (msg.Role == ChatRole.Assistant && msg.ToolCalls.Count > 0)
        {
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(msg.Content))
            {
                contents.Add(new TextContent(msg.Content));
            }

            foreach (var tc in msg.ToolCalls)
            {
                IDictionary<string, object?>? args = null;
                if (!string.IsNullOrEmpty(tc.ArgumentsJson))
                {
                    args = JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.ArgumentsJson);
                }

                contents.Add(new FunctionCallContent(tc.CallId, tc.Name, args));
            }

            return new AiChatMessage(role, contents);
        }

        // Build contents list, including media references if present
        if (msg.MediaReferences.Count > 0 && sessionDir is not null)
        {
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(msg.Content))
                contents.Add(new TextContent(msg.Content));

            foreach (var media in msg.MediaReferences)
            {
                var fullPath = Path.Combine(sessionDir, "media", media.RelativePath);
                if (!File.Exists(fullPath))
                {
                    logger?.LogWarning("Media file not found, skipping: {Path}", fullPath);
                    continue;
                }

                var bytes = File.ReadAllBytes(fullPath);
                contents.Add(new DataContent(bytes, media.MimeType));
            }

            if (contents.Count > 0)
                return new AiChatMessage(role, contents);
        }

        return new AiChatMessage(role, msg.Content);
    }

    public static List<AiChatMessage> ToAiMessages(
        IEnumerable<SerializableChatMessage> messages,
        string? sessionDir = null,
        ILogger? logger = null)
    {
        return [.. messages.Select(m => ToAiMessage(m, sessionDir, logger))];
    }

    public static SerializableChatMessage FromAiMessage(AiChatMessage msg, string? sessionDir = null)
    {
        var role = msg.Role == AiChatRole.User ? ChatRole.User
            : msg.Role == AiChatRole.Assistant ? ChatRole.Assistant
            : msg.Role == AiChatRole.System ? ChatRole.System
            : msg.Role == AiChatRole.Tool ? ChatRole.Tool
            : ChatRole.User;

        var content = string.Empty;
        var toolCalls = new List<SerializableToolCall>();
        var mediaRefs = new List<SerializableMediaReference>();
        string? toolCallId = null;

        // Extract structured content
        foreach (var c in msg.Contents)
        {
            switch (c)
            {
                case TextContent text:
                    // Append text (there may be text alongside tool calls)
                    content = string.IsNullOrEmpty(content)
                        ? text.Text ?? string.Empty
                        : content + (text.Text ?? string.Empty);
                    break;

                case FunctionCallContent toolCall:
                    var (meta, cleanArgs) = ExtractMeta(toolCall.Arguments);
                    toolCalls.Add(new SerializableToolCall
                    {
                        CallId = toolCall.CallId,
                        Name = toolCall.Name,
                        ArgumentsJson = cleanArgs is not null
                            ? JsonSerializer.Serialize(cleanArgs)
                            : string.Empty,
                        MetaJson = meta?.ToJson()
                    });
                    break;

                case FunctionResultContent toolResult:
                    toolCallId = toolResult.CallId;
                    content = toolResult.Result?.ToString() ?? string.Empty;
                    break;

                case DataContent data when sessionDir is not null:
                    var mediaRef = WriteMediaToSession(data, sessionDir);
                    if (mediaRef is not null)
                        mediaRefs.Add(mediaRef);
                    break;
            }
        }

        // Fallback: if no structured content was found, use .Text
        if (string.IsNullOrEmpty(content) && toolCalls.Count == 0
            && toolCallId is null && mediaRefs.Count == 0)
        {
            content = msg.Text ?? string.Empty;
        }

        return new SerializableChatMessage
        {
            Role = role,
            Content = content,
            ToolCalls = toolCalls,
            ToolCallId = toolCallId,
            MediaReferences = mediaRefs
        };
    }

    private static SerializableMediaReference? WriteMediaToSession(DataContent data, string sessionDir)
    {
        var mediaDir = Path.Combine(sessionDir, "media");
        Directory.CreateDirectory(mediaDir);

        var mimeType = data.MediaType ?? "application/octet-stream";
        var ext = MimeToExtension(mimeType);
        var fileName = $"{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(mediaDir, fileName);

        var bytes = data.Data.ToArray();
        if (bytes.Length == 0)
            return null;

        File.WriteAllBytes(fullPath, bytes);

        var modality = MimeToModality(mimeType);
        return new SerializableMediaReference
        {
            RelativePath = fileName,
            MimeType = mimeType,
            Modality = (int)modality
        };
    }

    internal static string MimeToExtension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "audio/mpeg" => ".mp3",
        "audio/wav" => ".wav",
        "video/mp4" => ".mp4",
        _ => ".bin"
    };

    internal static MediaModality MimeToModality(string mimeType)
    {
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return MediaModality.Image;
        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return MediaModality.Audio;
        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return MediaModality.Video;
        return MediaModality.Image; // default fallback
    }

    internal static (ToolCallMeta? Meta, IDictionary<string, object?>? CleanArgs) ExtractMeta(
        IDictionary<string, object?>? arguments) => ToolCallMeta.ExtractFrom(arguments);
}

// -----------------------------------------------------------------------
// <copyright file="ChatMessageConverter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Security;
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
    /// <summary>
    /// Convert a persisted message to a MEAI <see cref="AiChatMessage"/>.
    /// </summary>
    /// <param name="toolNameResolver">
    /// Optional canonical→LLM-facing tool-name resolver. Persisted tool
    /// call names are stored in canonical form (post-PR follow-up);
    /// when building a request that goes to an LLM provider (Anthropic,
    /// OpenAI) the names on <see cref="FunctionCallContent"/> must be
    /// the LLM-facing alias the model originally emitted. Pass
    /// <c>toolRegistry.ToLlmFacingName</c>. Null (default) leaves names
    /// untouched — appropriate for internal re-drive paths where we
    /// re-dispatch by canonical name through the registry's two-form
    /// lookup.
    /// </param>
    public static AiChatMessage ToAiMessage(
        SerializableChatMessage msg,
        string? sessionDir = null,
        ILogger? logger = null,
        Func<string, string>? toolNameResolver = null)
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
            var resultContent = new FunctionResultContent(msg.ToolCallId.Value.Value, msg.Content);
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

                var wireName = toolNameResolver?.Invoke(tc.Name.Value) ?? tc.Name.Value;
                contents.Add(new FunctionCallContent(tc.CallId.Value, wireName, args));
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
                var fullPath = SessionMediaStore.GetMediaPath(sessionDir, media.RelativePath);
                if (!File.Exists(fullPath))
                {
                    logger?.LogWarning("Media file not found, skipping: {Path}", fullPath);
                    continue;
                }

                var bytes = File.ReadAllBytes(fullPath);
                contents.Add(new DataContent(bytes, media.MimeType.Value));
            }

            if (contents.Count > 0)
                return new AiChatMessage(role, contents);
        }

        return new AiChatMessage(role, msg.Content);
    }

    public static List<AiChatMessage> ToAiMessages(
        IEnumerable<SerializableChatMessage> messages,
        string? sessionDir = null,
        ILogger? logger = null,
        Func<string, string>? toolNameResolver = null)
    {
        return [.. messages.Select(m => ToAiMessage(m, sessionDir, logger, toolNameResolver))];
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
                        CallId = new Netclaw.Tools.ToolCallId(toolCall.CallId),
                        Name = new Netclaw.Tools.ToolName(toolCall.Name),
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
                    var mediaRef = SessionMediaStore.WriteDataContent(data, sessionDir);
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
            ToolCallId = toolCallId is not null ? new Netclaw.Tools.ToolCallId(toolCallId) : null,
            MediaReferences = mediaRefs
        };
    }

    internal static string MimeToExtension(string mimeType) => MimeTypeCatalog.ExtensionFor(mimeType);

    internal static MediaModality MimeToModality(string mimeType)
    {
        if (MediaMimeClassifier.TryGetMediaModality(mimeType, out var modality))
            return modality;

        throw new ArgumentException($"Unsupported media MIME type: {mimeType}", nameof(mimeType));
    }

    internal static (ToolCallMeta? Meta, IDictionary<string, object?>? CleanArgs) ExtractMeta(
        IDictionary<string, object?>? arguments) => ToolCallMeta.ExtractFrom(arguments);
}

// -----------------------------------------------------------------------
// <copyright file="LlmResponseClassifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Actors.Sessions;

internal static class LlmResponseClassifier
{
    public static LlmResponseAnalysis Analyze(ChatMessage message)
    {
        var toolCalls = new List<FunctionCallContent>();
        bool hasText = false, hasThinking = false;
        int textChars = 0, thinkingChars = 0;

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    textChars += text.Text?.Length ?? 0;
                    if (!string.IsNullOrWhiteSpace(text.Text))
                        hasText = true;
                    break;
                case TextReasoningContent reasoning:
                    thinkingChars += reasoning.Text?.Length ?? 0;
                    if (!string.IsNullOrWhiteSpace(reasoning.Text))
                        hasThinking = true;
                    break;
                case FunctionCallContent toolCall:
                    toolCalls.Add(toolCall);
                    break;
            }
        }

        var kind =
            toolCalls.Count > 0 ? LlmResponseKind.ToolCalls
            : hasText ? LlmResponseKind.Text
            : hasThinking ? LlmResponseKind.ThinkingOnly
            : LlmResponseKind.Empty;

        return new LlmResponseAnalysis(toolCalls, kind, textChars, thinkingChars);
    }
}

/// <summary>
/// What the model produced on a turn. Tool calls take precedence over reply
/// text, and reply text over reasoning — so a response carrying both text and
/// tool calls classifies as <see cref="ToolCalls"/>.
/// </summary>
public enum LlmResponseKind
{
    /// <summary>Contains reply text — a normal answer to the user.</summary>
    Text,

    /// <summary>Requested one or more tool calls.</summary>
    ToolCalls,

    /// <summary>Emitted reasoning but no reply text and no tool calls.</summary>
    ThinkingOnly,

    /// <summary>No reply text, no reasoning, and no tool calls.</summary>
    Empty,
}

internal sealed record LlmResponseAnalysis(
    List<FunctionCallContent> ToolCalls,
    LlmResponseKind Kind,
    int TextChars,
    int ThinkingChars);

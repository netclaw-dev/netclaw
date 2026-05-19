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

        return new LlmResponseAnalysis(
            toolCalls,
            hasText,
            hasThinking,
            textChars,
            thinkingChars);
    }
}

internal sealed record LlmResponseAnalysis(
    List<FunctionCallContent> ToolCalls,
    bool HasText,
    bool HasThinking,
    int TextChars,
    int ThinkingChars);

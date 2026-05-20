// -----------------------------------------------------------------------
// <copyright file="LlmFailureClassifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http;
using Netclaw.Configuration;

namespace Netclaw.Actors.Sessions;

internal static class LlmFailureClassifier
{
    public static string ExtractUserMessage(Exception? cause, ModelCapabilities model)
    {
        if (cause is null)
            return "I encountered an error processing your message. Please try again.";

        var providerEx = FindException<ProviderException>(cause);
        if (providerEx is not null)
            return providerEx.UserMessage;

        if (IsContextOverflow(cause))
            return $"Context window exceeded after compaction — the session has too many tools or a large system prompt for the {model.ModelId} context window ({model.ContextWindowTokens} tokens). Try reducing tools or increasing the model's context window.";

        if (cause is TimeoutException)
            return "The LLM response stream timed out due to inactivity. The model may be overloaded or the context too large. Please try again.";

        return "I encountered an error processing your message. Please try again.";
    }

    public static bool IsContextOverflow(Exception? ex)
    {
        if (ex is null)
            return false;

        var providerEx = FindException<ProviderException>(ex);
        if (providerEx is { StatusCode: 400 } && ContainsOverflowKeyword(providerEx.Message))
            return true;

        var current = ex;
        while (current is not null)
        {
            if (ContainsOverflowKeyword(current.Message))
                return true;

            current = current.InnerException;
        }

        return false;
    }

    public static bool IsTransientStreaming(Exception? ex)
    {
        if (ex is null)
            return false;

        var providerEx = FindException<ProviderException>(ex);
        if (providerEx?.StatusCode is >= 500)
            return true;
        if (providerEx?.StatusCode is 429)
            return true;

        return ex is HttpRequestException { StatusCode: null };
    }

    private static T? FindException<T>(Exception? ex) where T : Exception
    {
        while (ex is not null)
        {
            if (ex is T match)
                return match;

            ex = ex.InnerException;
        }

        return null;
    }

    // Provider error formats differ; keyword matching is intentionally broad and
    // covered by ContextOverflowDetectionTests.
    private static bool ContainsOverflowKeyword(string message) =>
        message.Contains("context length", StringComparison.OrdinalIgnoreCase)
        || message.Contains("context_length", StringComparison.OrdinalIgnoreCase)
        || message.Contains("maximum context", StringComparison.OrdinalIgnoreCase)
        || message.Contains("exceeds", StringComparison.OrdinalIgnoreCase)
            && message.Contains("context", StringComparison.OrdinalIgnoreCase)
        || message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
        || message.Contains("token", StringComparison.OrdinalIgnoreCase)
            && message.Contains("exceed", StringComparison.OrdinalIgnoreCase);
}

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
    // Truncation cap on raw HTTP / exception messages we forward to the UI.
    // The provider's HttpRequestException.Message is normally short ("Connection
    // refused (host:port)") but SDK-wrapped exceptions can be much longer and
    // sometimes embed echoed request fragments. Cap so a chat window never gets
    // a 2KB error blob.
    private const int MaxForwardedMessageLength = 200;

    public static string ExtractUserMessage(Exception? cause, ModelCapabilities model)
    {
        if (cause is null)
            return GenericFailureMessage;

        // ProviderException already carries a user-safe message — provider
        // transport layers (OpenAiCompatibleChatClient etc.) curate this so
        // we don't have to.
        var providerEx = FindException<ProviderException>(cause);
        if (providerEx is not null)
            return providerEx.UserMessage;

        if (IsContextOverflow(cause))
            return $"Context window exceeded after compaction — the session has too many tools or a large system prompt for the {model.ModelId} context window ({model.ContextWindowTokens} tokens). Try reducing tools or increasing the model's context window.";

        if (cause is TimeoutException)
            return "The LLM response stream timed out due to inactivity. The model may be overloaded or the context too large. Please try again.";

        // Raw HTTP transport failure not wrapped in ProviderException — common
        // when SDKs like OllamaSharp throw HttpRequestException directly, or
        // when the request never reaches a provider (DNS / connection refused).
        // The exception's StatusCode is null for pre-response failures and
        // populated for response-derived ones.
        var httpEx = FindException<HttpRequestException>(cause);
        if (httpEx is not null)
            return FormatHttpFailure(httpEx);

        // Final catch-all. Surfacing the exception type + a truncated message
        // beats the historical "please try again" wall — at minimum the
        // operator sees what kind of failure it was.
        return $"Unexpected LLM provider error ({cause.GetType().Name}): {Truncate(cause.Message)}";
    }

    private const string GenericFailureMessage =
        "I encountered an error processing your message. Please try again.";

    private static string FormatHttpFailure(HttpRequestException ex)
    {
        var status = (int?)ex.StatusCode;
        return status switch
        {
            401 or 403 => $"LLM provider rejected the request (HTTP {status}): authentication failed or revoked. Re-run 'netclaw provider add <name> ...' or check stored credentials.",
            429        => "LLM provider rate-limited the request (HTTP 429). Wait a moment and try again.",
            >= 500     => $"LLM provider returned a server error (HTTP {status}). The provider may be overloaded. Please try again.",
            not null   => $"LLM provider returned HTTP {status}: {Truncate(ex.Message)}",
            null       => $"LLM provider transport error: {Truncate(ex.Message)}. Check provider configuration and connectivity.",
        };
    }

    private static string Truncate(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty :
        s.Length <= MaxForwardedMessageLength ? s :
        s[..MaxForwardedMessageLength] + "…";

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

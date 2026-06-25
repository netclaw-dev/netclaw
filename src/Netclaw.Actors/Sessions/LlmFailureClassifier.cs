// -----------------------------------------------------------------------
// <copyright file="LlmFailureClassifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
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

        var clientResultEx = FindSdkResponseException(cause);
        if (clientResultEx is not null)
            return FormatSdkResponseFailure(clientResultEx);

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

    private static string FormatSdkResponseFailure(Exception ex)
    {
        var status = ReadIntProperty(ex, "Status");
        var detail = ExtractSdkResponseDetail(ex);
        var message = string.IsNullOrWhiteSpace(detail) ? ex.Message : detail;

        return status switch
        {
            400 => $"LLM provider rejected the request as malformed (HTTP 400): {Truncate(RedactSensitive(message))}",
            401 or 403 => $"LLM provider rejected the request (HTTP {status}): authentication failed or revoked. Re-run 'netclaw provider add <name> ...' or check stored credentials.",
            429 => "LLM provider rate-limited the request (HTTP 429). Wait a moment and try again.",
            >= 500 => $"LLM provider returned a server error (HTTP {status}). The provider may be overloaded. Please try again.",
            > 0 => $"LLM provider returned HTTP {status}: {Truncate(RedactSensitive(message))}",
            _ => $"LLM provider request failed: {Truncate(RedactSensitive(message))}",
        };
    }

    // OpenAI/System.ClientModel exceptions live in provider SDK assemblies,
    // not in Netclaw.Actors. Keep this boundary dependency-free by matching the
    // stable exception shape (ClientResultException + Status) instead of adding
    // a direct reference just to improve operator diagnostics.
    private static Exception? FindSdkResponseException(Exception? ex)
    {
        while (ex is not null)
        {
            var type = ex.GetType();
            if (type.Name.Contains("ClientResultException", StringComparison.Ordinal)
                && ReadIntProperty(ex, "Status") is not null)
            {
                return ex;
            }

            ex = ex.InnerException;
        }

        return null;
    }

    // Reflection here is intentionally narrow: public integer properties only.
    // It lets us read SDK status codes without broad dynamic dispatch or a new
    // actors-layer package dependency.
    private static int? ReadIntProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public);
        var value = property?.GetValue(instance);
        return value switch
        {
            int i => i,
            uint u when u <= int.MaxValue => (int)u,
            _ => null,
        };
    }

    // ClientResultException.GetRawResponse().Content contains the provider's
    // real error body. For GHE/Copilot this is the difference between a generic
    // HTTP 400 and actionable messages like invalid API version, token stamp,
    // or model policy failures. Treat it as optional because SDKs can throw
    // before a response body exists.
    private static string? ExtractSdkResponseDetail(Exception ex)
    {
        try
        {
            var rawResponse = ex.GetType()
                .GetMethod("GetRawResponse", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes)
                ?.Invoke(ex, null);
            var content = rawResponse?.GetType()
                .GetProperty("Content", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(rawResponse);
            var text = content?.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Error bodies can echo auth headers or configured GitHub tokens. Redact
    // before forwarding details to chat/log surfaces; truncation alone is not
    // enough when a secret appears near the start of the message.
    private static string RedactSensitive(string value)
    {
        var redacted = Regex.Replace(
            value,
            @"Bearer\s+[^\s,'""}]+",
            "Bearer [redacted]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(
            redacted,
            @"github_pat_[A-Za-z0-9_]+",
            "github_pat_[redacted]",
            RegexOptions.CultureInvariant);
    }

    private static string Truncate(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty :
        s.Length <= MaxForwardedMessageLength ? s :
        s[..MaxForwardedMessageLength] + "…";

    public static bool IsContextOverflow(Exception? ex)
    {
        if (ex is null)
            return false;

        // Structural BadRequest errors must NEVER classify as context overflow.
        // vLLM and other strict OpenAI-compatible servers return 400 for
        // wire-format violations like a non-leading System message ("System
        // message must be at the beginning."). Routing those through the
        // overflow path triggers a doomed compaction → retry → second 400,
        // ending in the misleading user message "Context window exceeded
        // even after compaction." Compaction cannot fix a wire-format bug.
        //
        // Gate strictly on ProviderException{StatusCode:400}: a 5xx whose
        // chain incidentally contains a structural keyword (e.g.,
        // "invalid role configuration in proxy") must NOT suppress overflow
        // classification on the outer exception.
        var providerEx = FindException<ProviderException>(ex);
        if (providerEx is { StatusCode: 400 } && ContainsStructuralBadRequestKeyword(providerEx.Message))
            return false;

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

    // Wire-format / request-structure errors. These are 400s that mean
    // "your request is malformed", not "your request is too big". They
    // must short-circuit overflow detection so we don't trigger a
    // doomed compaction loop on a structural bug.
    private static bool ContainsStructuralBadRequestKeyword(string message) =>
        message.Contains("System message must be at the beginning", StringComparison.OrdinalIgnoreCase)
        || message.Contains("must alternate", StringComparison.OrdinalIgnoreCase)
        || message.Contains("invalid role", StringComparison.OrdinalIgnoreCase)
        || message.Contains("tools` must not be an empty array", StringComparison.Ordinal);
}

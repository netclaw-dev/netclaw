using System.Text.Json;

namespace Netclaw.Providers;

/// <summary>
/// Shared error message extraction for OpenAI-compatible provider error responses.
/// Parses <c>{"error": {"message": "..."}}</c> and <c>{"error": "..."}</c> formats,
/// with per-provider status code fallback tables.
/// </summary>
public static class ProviderErrorHelper
{
    /// <summary>
    /// Extracts a user-friendly error message from an OpenAI-compatible error response body.
    /// Falls back to <paramref name="statusCodeFallback"/> if the body can't be parsed.
    /// </summary>
    /// <param name="responseBody">The raw HTTP response body (may be null, empty, JSON, or HTML).</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="providerLabel">Label for error messages (e.g. "OpenAI Codex", "LLM provider").</param>
    /// <param name="statusCodeFallback">
    /// Optional per-provider fallback for specific status codes.
    /// Called when the body doesn't contain a parseable error message.
    /// Return null to use the default fallback.
    /// </param>
    public static string ExtractUserMessage(
        string? responseBody,
        int statusCode,
        string providerLabel,
        Func<int, string?>? statusCodeFallback = null)
    {
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("message", out var msg)
                        && msg.ValueKind == JsonValueKind.String
                        && msg.GetString() is { Length: > 0 } errorMessage)
                    {
                        return $"{providerLabel} error ({statusCode}): {errorMessage}";
                    }

                    if (error.ValueKind == JsonValueKind.String
                        && error.GetString() is { Length: > 0 } simpleError)
                    {
                        return $"{providerLabel} error ({statusCode}): {simpleError}";
                    }
                }
            }
            catch (JsonException)
            {
                return $"{providerLabel} returned an error (HTTP {statusCode}). Response was not valid JSON.";
            }
        }

        if (statusCodeFallback?.Invoke(statusCode) is { } custom)
            return custom;

        return statusCode switch
        {
            401 or 403 => $"{providerLabel} rejected the request \u2014 check credentials.",
            429 => $"{providerLabel} is rate-limiting requests. Try again shortly.",
            >= 500 => $"{providerLabel} returned a server error ({statusCode}). The provider may be experiencing issues.",
            _ => $"{providerLabel} returned an error (HTTP {statusCode}). Please try again."
        };
    }
}

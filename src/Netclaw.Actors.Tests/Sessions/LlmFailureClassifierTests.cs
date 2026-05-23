// -----------------------------------------------------------------------
// <copyright file="LlmFailureClassifierTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public class LlmFailureClassifierTests
{
    private static readonly ModelCapabilities Model = new()
    {
        ModelId = "test-model",
        ContextWindowTokens = 8000,
    };

    [Fact]
    public void NullCause_ReturnsGenericMessage()
    {
        var message = LlmFailureClassifier.ExtractUserMessage(null, Model);

        Assert.Equal("I encountered an error processing your message. Please try again.", message);
    }

    [Fact]
    public void ProviderException_UsesProviderUserMessage()
    {
        // Provider-curated messages bypass our heuristics — the provider
        // already decided what's safe to surface.
        var ex = new ProviderException("Custom provider message", "internal detail", statusCode: 500);

        var message = LlmFailureClassifier.ExtractUserMessage(ex, Model);

        Assert.Equal("Custom provider message", message);
    }

    [Fact]
    public void TimeoutException_GetsTimeoutMessage()
    {
        var message = LlmFailureClassifier.ExtractUserMessage(new TimeoutException("idle"), Model);

        Assert.Contains("timed out", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    public void HttpRequestException_AuthStatus_PromptsReauth(HttpStatusCode status, string statusText)
    {
        var ex = new HttpRequestException("auth rejected", inner: null, statusCode: status);

        var message = LlmFailureClassifier.ExtractUserMessage(ex, Model);

        Assert.Contains(statusText, message);
        Assert.Contains("netclaw provider add", message);
    }

    [Fact]
    public void HttpRequestException_429_IsNamedAsRateLimit()
    {
        var ex = new HttpRequestException("too many", inner: null, statusCode: HttpStatusCode.TooManyRequests);

        var message = LlmFailureClassifier.ExtractUserMessage(ex, Model);

        Assert.Contains("rate-limited", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("429", message);
    }

    [Fact]
    public void HttpRequestException_5xx_IsNamedAsServerError()
    {
        var ex = new HttpRequestException("upstream offline", inner: null,
            statusCode: HttpStatusCode.BadGateway);

        var message = LlmFailureClassifier.ExtractUserMessage(ex, Model);

        Assert.Contains("server error", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("502", message);
    }

    [Fact]
    public void HttpRequestException_NullStatus_SurfacesTransportDetail()
    {
        // This is the case the user actually hit — OllamaSharp threw
        // HttpRequestException("Connection refused (localhost:11434)") with
        // no StatusCode, and the old classifier swallowed it.
        var ex = new HttpRequestException("Connection refused (localhost:11434)");

        var message = LlmFailureClassifier.ExtractUserMessage(ex, Model);

        Assert.Contains("transport error", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection refused (localhost:11434)", message);
    }

    [Fact]
    public void HttpRequestException_NestedInInvocationException_IsStillUnwrapped()
    {
        // Akka and task-based pipelines often wrap the real cause in
        // outer exceptions; the classifier must walk the chain.
        var inner = new HttpRequestException("Connection refused (host:1234)");
        var wrapped = new InvalidOperationException("LLM call failed", inner);

        var message = LlmFailureClassifier.ExtractUserMessage(wrapped, Model);

        Assert.Contains("transport error", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection refused (host:1234)", message);
    }

    [Fact]
    public void UnknownException_SurfacesTypeAndTruncatedMessage()
    {
        var ex = new InvalidOperationException("something specific went wrong");

        var message = LlmFailureClassifier.ExtractUserMessage(ex, Model);

        Assert.Contains("InvalidOperationException", message);
        Assert.Contains("something specific went wrong", message);
    }

    [Fact]
    public void UnknownException_LongMessage_GetsTruncated()
    {
        var longMessage = new string('x', 1000);
        var ex = new InvalidOperationException(longMessage);

        var message = LlmFailureClassifier.ExtractUserMessage(ex, Model);

        Assert.True(message.Length < 500,
            $"forwarded message length should be capped; got {message.Length}");
        Assert.EndsWith("…", message);
    }

    [Fact]
    public void ContextOverflow_NamesTheModelAndContextWindow()
    {
        var ex = new InvalidOperationException("prompt is too long for the context");

        var message = LlmFailureClassifier.ExtractUserMessage(ex, Model);

        Assert.Contains("test-model", message);
        Assert.Contains("8000", message);
    }
}

using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Tests for <see cref="LlmSessionActor.IsContextOverflowError"/> to ensure
/// context overflow detection works across multiple LLM providers.
/// </summary>
public sealed class ContextOverflowDetectionTests
{
    [Theory]
    [InlineData("request (66540 tokens) exceeds the available context size (65536 tokens)")]  // llama-server
    [InlineData("context length exceeded")]  // Ollama
    [InlineData("This model's maximum context length is 8192 tokens")]  // OpenAI
    [InlineData("context_length_exceeded")]  // OpenAI error code
    [InlineData("prompt is too long: 12000 tokens > 8192 maximum")]  // Anthropic-style
    [InlineData("Input token count (65000) exceeds model limit (32768)")]  // vLLM-style
    public void Detects_overflow_from_plain_exception(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.True(LlmSessionActor.IsContextOverflowError(ex));
    }

    [Theory]
    [InlineData("request (66540 tokens) exceeds the available context size (65536 tokens)")]
    [InlineData("context length exceeded")]
    [InlineData("prompt is too long")]
    public void Detects_overflow_from_provider_exception_with_400(string message)
    {
        var ex = new ProviderException(message, message, statusCode: 400);
        Assert.True(LlmSessionActor.IsContextOverflowError(ex));
    }

    [Fact]
    public void Detects_overflow_in_inner_exception()
    {
        var inner = new InvalidOperationException("context length exceeded");
        var outer = new Exception("LLM call failed", inner);
        Assert.True(LlmSessionActor.IsContextOverflowError(outer));
    }

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("model not found")]
    [InlineData("invalid API key")]
    [InlineData("server error")]
    public void Does_not_false_positive_on_unrelated_errors(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.False(LlmSessionActor.IsContextOverflowError(ex));
    }

    [Fact]
    public void Returns_false_for_null()
    {
        Assert.False(LlmSessionActor.IsContextOverflowError(null));
    }

    [Fact]
    public void Provider_exception_with_non_400_status_uses_keyword_fallback()
    {
        // 500 status but overflow message — should still detect via keyword fallback
        var ex = new ProviderException("context length exceeded", "context length exceeded", statusCode: 500);
        Assert.True(LlmSessionActor.IsContextOverflowError(ex));
    }
}

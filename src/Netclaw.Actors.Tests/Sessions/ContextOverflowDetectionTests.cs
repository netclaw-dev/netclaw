// -----------------------------------------------------------------------
// <copyright file="ContextOverflowDetectionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

    [Theory]
    [InlineData("System message must be at the beginning.")]                  // vLLM verbatim (post-#1171 regression)
    [InlineData("system message must be at the beginning")]                   // case-insensitive
    [InlineData("messages must alternate between user and assistant")]        // generic shape rule
    [InlineData("invalid role 'developer' for this model")]                   // unsupported role
    public void Structural_badrequest_does_not_classify_as_overflow(string message)
    {
        // These are wire-format violations, not "too many tokens". Routing
        // them through the overflow path triggers a doomed compact-and-retry
        // loop that ends in the misleading "Context window exceeded even
        // after compaction" user-visible message. Compaction cannot fix
        // wire-format bugs; the classifier must short-circuit.
        var providerEx = new ProviderException(message, message, statusCode: 400);
        Assert.False(LlmSessionActor.IsContextOverflowError(providerEx));

        // Also covered when the structural message hides in an inner exception.
        var wrapped = new Exception("LLM streaming failed", providerEx);
        Assert.False(LlmSessionActor.IsContextOverflowError(wrapped));
    }

    [Fact]
    public void Structural_badrequest_wins_even_if_message_also_mentions_context()
    {
        // Defensive: if a provider emitted both keywords (unlikely but
        // not impossible), the structural classification still wins so
        // we don't compaction-loop.
        const string msg = "System message must be at the beginning. Note: also exceeded context length.";
        var ex = new ProviderException(msg, msg, statusCode: 400);
        Assert.False(LlmSessionActor.IsContextOverflowError(ex));
    }

    [Fact]
    public void Non_400_with_structural_keyword_does_not_suppress_overflow_classification()
    {
        // A 5xx (or non-400) exception whose message incidentally contains
        // a structural keyword must NOT suppress overflow detection. The
        // structural short-circuit is gated on ProviderException{StatusCode:400}.
        const string msg = "upstream proxy returned invalid role configuration; context length exceeded retrying";
        var ex = new ProviderException(msg, msg, statusCode: 500);
        Assert.True(LlmSessionActor.IsContextOverflowError(ex));
    }

    [Fact]
    public void Non_400_with_only_structural_keyword_is_not_overflow()
    {
        // 5xx without any overflow keyword should not classify as overflow
        // regardless of structural-keyword presence (control case).
        const string msg = "upstream proxy returned invalid role configuration";
        var ex = new ProviderException(msg, msg, statusCode: 500);
        Assert.False(LlmSessionActor.IsContextOverflowError(ex));
    }
}

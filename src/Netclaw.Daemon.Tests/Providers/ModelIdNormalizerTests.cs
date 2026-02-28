using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class ModelIdNormalizerTests
{
    [Fact]
    public void OriginalId_AlwaysFirst()
    {
        var candidates = ModelIdNormalizer.GetCandidates("claude-sonnet-4-20250514");
        Assert.Equal("claude-sonnet-4-20250514", candidates[0]);
    }

    [Fact]
    public void StripDateSuffix()
    {
        var candidates = ModelIdNormalizer.GetCandidates("claude-sonnet-4-20250514");
        Assert.Contains("claude-sonnet-4", candidates);
    }

    [Fact]
    public void AddProviderPrefix_Claude()
    {
        var candidates = ModelIdNormalizer.GetCandidates("claude-sonnet-4-20250514");
        Assert.Contains("anthropic/claude-sonnet-4-20250514", candidates);
        Assert.Contains("anthropic/claude-sonnet-4", candidates);
    }

    [Fact]
    public void AddProviderPrefix_Gpt()
    {
        var candidates = ModelIdNormalizer.GetCandidates("gpt-4o");
        Assert.Contains("openai/gpt-4o", candidates);
    }

    [Fact]
    public void StripOllamaTag()
    {
        var candidates = ModelIdNormalizer.GetCandidates("llava:latest");
        Assert.Contains("llava", candidates);
    }

    [Fact]
    public void StripOllamaTag_WithVersion()
    {
        var candidates = ModelIdNormalizer.GetCandidates("qwen3:30b");
        Assert.Contains("qwen3", candidates);
        Assert.Contains("qwen/qwen3", candidates);
    }

    [Fact]
    public void AlreadyPrefixed_StripDateOnly()
    {
        var candidates = ModelIdNormalizer.GetCandidates("anthropic/claude-sonnet-4-20250514");
        Assert.Contains("anthropic/claude-sonnet-4", candidates);
        // Should not double-prefix
        Assert.DoesNotContain("anthropic/anthropic/claude-sonnet-4", candidates);
    }

    [Fact]
    public void NoTransformNeeded()
    {
        var candidates = ModelIdNormalizer.GetCandidates("anthropic/claude-sonnet-4");
        Assert.Equal("anthropic/claude-sonnet-4", candidates[0]);
    }
}

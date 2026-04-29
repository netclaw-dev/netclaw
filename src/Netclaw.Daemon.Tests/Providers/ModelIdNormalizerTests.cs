// -----------------------------------------------------------------------
// <copyright file="ModelIdNormalizerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class ModelIdNormalizerTests
{
    // === Existing regression tests ===

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

    // === GGUF extension stripping ===

    [Fact]
    public void StripGgufExtension()
    {
        var candidates = ModelIdNormalizer.GetCandidates("Model-Q5_K_M.gguf");
        Assert.Contains("Model-Q5_K_M", candidates);
    }

    [Fact]
    public void StripGgufExtension_CaseInsensitive()
    {
        var candidates = ModelIdNormalizer.GetCandidates("Model-Q5_K_M.GGUF");
        Assert.Contains("Model-Q5_K_M", candidates);
    }

    // === Quantization suffix stripping ===

    [Theory]
    [InlineData("model-Q4_0", "model")]
    [InlineData("model-Q5_K_M", "model")]
    [InlineData("model-Q8_0", "model")]
    [InlineData("model-IQ2_XXS", "model")]
    [InlineData("model-Q4_K_XL", "model")]
    [InlineData("model-IQ3_M", "model")]
    [InlineData("model-q4_0", "model")]
    public void StripQuantizationSuffix(string input, string expected)
    {
        var candidates = ModelIdNormalizer.GetCandidates(input);
        Assert.Contains(expected, candidates);
    }

    // === Combined GGUF + quant ===

    [Fact]
    public void CombinedGgufAndQuant()
    {
        var candidates = ModelIdNormalizer.GetCandidates("Qwen2.5-Coder-32B-Instruct-Q5_K_M.gguf");
        Assert.Contains("Qwen2.5-Coder-32B-Instruct", candidates);
    }

    // === Lowercase normalization ===

    [Fact]
    public void LowercaseNormalization()
    {
        var candidates = ModelIdNormalizer.GetCandidates("Qwen2.5-Coder-32B-Instruct");
        Assert.Contains("qwen2.5-coder-32b-instruct", candidates);
    }

    // === Full pipeline ===

    [Fact]
    public void FullPipeline_GgufToLowercase()
    {
        var candidates = ModelIdNormalizer.GetCandidates("Qwen2.5-Coder-32B-Instruct-Q5_K_M.gguf");
        Assert.Contains("qwen2.5-coder-32b-instruct", candidates);
    }

    // === Trailing segment stripping ===

    [Fact]
    public void TrailingSegmentStrip()
    {
        var candidates = ModelIdNormalizer.GetCandidates("qwen3.5-35b-a3b-ud");
        Assert.Contains("qwen3.5-35b-a3b", candidates);
    }

    // === Current running model: Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf ===

    [Fact]
    public void CurrentRunningModel_ProducesNormalizedCandidate()
    {
        var candidates = ModelIdNormalizer.GetCandidates("Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf");
        Assert.Contains("qwen3.5-35b-a3b", candidates);
    }

    // === Simple quant with existing prefix ===

    [Fact]
    public void SimpleQuant_WithExistingPrefix()
    {
        var candidates = ModelIdNormalizer.GetCandidates("llama-3.1-8b-q4_0");
        Assert.Contains("llama-3.1-8b", candidates);
        Assert.Contains("meta-llama/llama-3.1-8b", candidates);
    }

    // === False positive guard ===

    [Fact]
    public void FalsePositiveGuard_NoQuantStrip()
    {
        var candidates = ModelIdNormalizer.GetCandidates("Qwen3-30B");
        // "30B" is not a quantization pattern — should not be stripped
        Assert.Contains("Qwen3-30B", candidates);
        Assert.DoesNotContain("Qwen3", candidates.Where(c =>
            !c.Contains('/', StringComparison.Ordinal) &&
            c == "Qwen3").ToList());
    }

    // === GetDisplayName tests ===

    [Theory]
    [InlineData("Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf", "Qwen3.5-35B-A3B-UD")]
    [InlineData("qwen3:30b", "qwen3")]
    [InlineData("claude-sonnet-4-20250514", "claude-sonnet-4-20250514")]
    [InlineData("aaron/custom-llama:latest", "aaron/custom-llama")]
    [InlineData("Meta-Llama-3.1-8B-Instruct-Q4_0.gguf", "Meta-Llama-3.1-8B-Instruct")]
    public void GetDisplayName_StripsFileFormatNoise(string input, string expected)
    {
        Assert.Equal(expected, ModelIdNormalizer.GetDisplayName(input));
    }
}

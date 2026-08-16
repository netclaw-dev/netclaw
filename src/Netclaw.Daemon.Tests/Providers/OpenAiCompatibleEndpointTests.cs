// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenAiCompatibleEndpointTests
{
    [Theory]
    [InlineData("https://api.z.ai/api/coding/paas/v4", "/api/coding/paas/v4/chat/completions", "/api/coding/paas/v4/models")]
    [InlineData("https://api.z.ai/api/paas/v4", "/api/paas/v4/chat/completions", "/api/paas/v4/models")]
    [InlineData("https://api.deepseek.com/v1", "/v1/chat/completions", "/v1/models")]
    [InlineData("http://localhost:8000/api/v1", "/api/v1/chat/completions", "/api/v1/models")]
    [InlineData("https://example.test/v2", "/v2/chat/completions", "/v2/models")]
    [InlineData("https://example.test/v4/", "/v4/chat/completions", "/v4/models")]
    public void FromBaseUrl_TrailingVersionSegmentIsAlreadyVersioned(
        string endpoint, string expectedChatPath, string expectedModelsPath)
    {
        var result = OpenAiCompatibleEndpoint.FromBaseUrl(endpoint);

        Assert.Equal(expectedChatPath, result.ChatCompletionsPath);
        Assert.Equal(expectedModelsPath, result.ModelsPath);
    }

    [Theory]
    [InlineData("http://localhost:8000", "/v1/chat/completions", "/v1/models")]
    [InlineData("http://localhost:8000/", "/v1/chat/completions", "/v1/models")]
    [InlineData("http://localhost:8000/edge", "/edge/v1/chat/completions", "/edge/v1/models")]
    [InlineData("https://example.test/vendor", "/vendor/v1/chat/completions", "/vendor/v1/models")]
    public void FromBaseUrl_UnversionedBaseGetsV1Default(
        string endpoint, string expectedChatPath, string expectedModelsPath)
    {
        var result = OpenAiCompatibleEndpoint.FromBaseUrl(endpoint);

        Assert.Equal(expectedChatPath, result.ChatCompletionsPath);
        Assert.Equal(expectedModelsPath, result.ModelsPath);
    }

    [Fact]
    public void FromBaseUrl_DoesNotTreatVersionLikeWordsAsVersions()
    {
        // A segment like "vendor" or "vpreview" must not suppress the /v1 default.
        var result = OpenAiCompatibleEndpoint.FromBaseUrl("https://example.test/vpreview");

        Assert.Equal("/vpreview/v1/chat/completions", result.ChatCompletionsPath);
    }

    [Fact]
    public void FromBaseUrl_PassesApiKeyThrough()
    {
        var result = OpenAiCompatibleEndpoint.FromBaseUrl("https://api.z.ai/api/coding/paas/v4", "key");

        Assert.Equal("key", result.ApiKey);
        Assert.Equal("https://api.z.ai/api/coding/paas/v4", result.BaseUri.AbsoluteUri);
    }
}

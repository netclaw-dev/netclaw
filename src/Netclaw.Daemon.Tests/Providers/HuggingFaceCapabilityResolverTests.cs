// -----------------------------------------------------------------------
// <copyright file="HuggingFaceCapabilityResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class HuggingFaceCapabilityResolverTests
{
    [Fact]
    public void ParseModelInfo_ImageTextToText()
    {
        const string json = """
        {
          "modelId": "llava-hf/llava-1.5-7b-hf",
          "pipeline_tag": "image-text-to-text"
        }
        """;

        var result = HuggingFaceCapabilityResolver.ParseModelInfo("llava", json);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ParseModelInfo_TextGeneration()
    {
        const string json = """
        {
          "modelId": "mistralai/Mistral-7B-v0.1",
          "pipeline_tag": "text-generation"
        }
        """;

        var result = HuggingFaceCapabilityResolver.ParseModelInfo("mistral-7b", json);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ParseModelInfo_NoPipelineTag_ReturnsNull()
    {
        const string json = """
        {
          "modelId": "some/model"
        }
        """;

        var result = HuggingFaceCapabilityResolver.ParseModelInfo("some/model", json);

        Assert.Null(result);
    }

    [Fact]
    public void ParseModelInfo_AutomaticSpeechRecognition()
    {
        const string json = """
        {
          "modelId": "openai/whisper-large-v3",
          "pipeline_tag": "automatic-speech-recognition"
        }
        """;

        var result = HuggingFaceCapabilityResolver.ParseModelInfo("whisper", json);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Audio, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Theory]
    [InlineData("text-to-image", ModelModality.Text, ModelModality.Image)]
    [InlineData("text-to-audio", ModelModality.Text, ModelModality.Audio)]
    [InlineData("text-to-video", ModelModality.Text, ModelModality.Video)]
    [InlineData("video-text-to-text", ModelModality.Text | ModelModality.Video, ModelModality.Text)]
    public void MapPipelineTag_KnownTags(string tag, ModelModality expectedInput, ModelModality expectedOutput)
    {
        var (input, output) = HuggingFaceCapabilityResolver.MapPipelineTag(tag);

        Assert.Equal(expectedInput, input);
        Assert.Equal(expectedOutput, output);
    }

    [Fact]
    public void MapPipelineTag_UnknownTag_DefaultsToText()
    {
        var (input, output) = HuggingFaceCapabilityResolver.MapPipelineTag("something-new");

        Assert.Equal(ModelModality.Text, input);
        Assert.Equal(ModelModality.Text, output);
    }
}

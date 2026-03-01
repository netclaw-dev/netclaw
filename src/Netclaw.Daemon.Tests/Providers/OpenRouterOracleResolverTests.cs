using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenRouterOracleResolverTests
{
    [Fact]
    public void ParseCatalog_MultimodalModel()
    {
        const string json = """
        {
          "data": [
            {
              "id": "anthropic/claude-sonnet-4",
              "architecture": {
                "input_modalities": ["text", "image"],
                "output_modalities": ["text"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        Assert.True(catalog.ContainsKey("anthropic/claude-sonnet-4"));
        var caps = catalog["anthropic/claude-sonnet-4"];
        Assert.Equal(ModelModality.Text | ModelModality.Image, caps.InputModalities);
        Assert.Equal(ModelModality.Text, caps.OutputModalities);
    }

    [Fact]
    public void ParseCatalog_TextOnlyModel()
    {
        const string json = """
        {
          "data": [
            {
              "id": "mistralai/mistral-7b",
              "architecture": {
                "input_modalities": ["text"],
                "output_modalities": ["text"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        var caps = catalog["mistralai/mistral-7b"];
        Assert.Equal(ModelModality.Text, caps.InputModalities);
        Assert.Equal(ModelModality.Text, caps.OutputModalities);
    }

    [Fact]
    public void ParseCatalog_FullMultimodal()
    {
        const string json = """
        {
          "data": [
            {
              "id": "openai/gpt-4o",
              "architecture": {
                "input_modalities": ["text", "image", "audio"],
                "output_modalities": ["text", "audio"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        var caps = catalog["openai/gpt-4o"];
        Assert.Equal(ModelModality.Text | ModelModality.Image | ModelModality.Audio, caps.InputModalities);
        Assert.Equal(ModelModality.Text | ModelModality.Audio, caps.OutputModalities);
    }

    [Fact]
    public void ParseCatalog_NoArchitecture_DefaultsToText()
    {
        const string json = """
        {
          "data": [
            {
              "id": "some/model"
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        var caps = catalog["some/model"];
        Assert.Equal(ModelModality.Text, caps.InputModalities);
        Assert.Equal(ModelModality.Text, caps.OutputModalities);
    }

    [Fact]
    public void ParseCatalog_ContextLength_Parsed()
    {
        const string json = """
        {
          "data": [
            {
              "id": "qwen/qwen3.5-35b-a3b",
              "context_length": 262144,
              "architecture": {
                "input_modalities": ["text"],
                "output_modalities": ["text"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        var caps = catalog["qwen/qwen3.5-35b-a3b"];
        Assert.Equal(262_144, caps.ContextWindowTokens);
    }

    [Fact]
    public void ParseCatalog_NoContextLength_ReturnsNull()
    {
        const string json = """
        {
          "data": [
            {
              "id": "some/model",
              "architecture": {
                "input_modalities": ["text"],
                "output_modalities": ["text"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        Assert.Null(catalog["some/model"].ContextWindowTokens);
    }

    [Fact]
    public void ParseCatalog_EmptyData()
    {
        const string json = """{"data": []}""";

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        Assert.Empty(catalog);
    }

    [Fact]
    public void ParseModalityArray_UnknownValues_Ignored()
    {
        const string json = """["text", "image", "hologram"]""";
        var element = System.Text.Json.JsonDocument.Parse(json).RootElement;

        var result = OpenRouterOracleResolver.ParseModalityArray(element);

        Assert.Equal(ModelModality.Text | ModelModality.Image, result);
    }
}

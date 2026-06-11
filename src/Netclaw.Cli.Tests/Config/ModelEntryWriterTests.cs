// -----------------------------------------------------------------------
// <copyright file="ModelEntryWriterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

/// <summary>
/// Guards the single persist path shared by `model set`, the init wizard, and the TUI
/// model manager. The rule under test: modalities are written only when the discovery
/// source genuinely reported them, so an unknown is never frozen into config as a
/// permanent "Text" override (#1290).
/// </summary>
public class ModelEntryWriterTests
{
    [Fact]
    public void BuildModelEntry_UnknownModalities_OmitsModalityKeys()
    {
        var entry = ModelEntryWriter.BuildModelEntry(
            "my-vllm", "qwen-vl", ModelDiscoverySource.Live,
            contextWindow: 32768, inputModalities: null, outputModalities: null);

        Assert.Equal("my-vllm", entry["Provider"]);
        Assert.Equal("qwen-vl", entry["ModelId"]);
        Assert.Equal("Live", entry["Provenance"]);
        Assert.Equal(32768, entry["ContextWindow"]);
        Assert.False(entry.ContainsKey("InputModalities"));
        Assert.False(entry.ContainsKey("OutputModalities"));
    }

    [Fact]
    public void BuildModelEntry_KnownModalities_WritesThem()
    {
        var entry = ModelEntryWriter.BuildModelEntry(
            "openai-codex", "gpt-x", ModelDiscoverySource.Live,
            contextWindow: null,
            inputModalities: ModelModality.Text | ModelModality.Image,
            outputModalities: ModelModality.Text);

        Assert.Equal("Text, Image", entry["InputModalities"]);
        Assert.Equal("Text", entry["OutputModalities"]);
        Assert.False(entry.ContainsKey("ContextWindow"));
    }

    [Fact]
    public void BuildModelEntry_BlankModelIdAndNullProvenance_OmitsBoth()
    {
        var entry = ModelEntryWriter.BuildModelEntry(
            "p", modelId: "", provenance: null,
            contextWindow: null, inputModalities: null, outputModalities: null);

        Assert.True(entry.ContainsKey("Provider"));
        Assert.False(entry.ContainsKey("ModelId"));
        Assert.False(entry.ContainsKey("Provenance"));
    }
}

// -----------------------------------------------------------------------
// <copyright file="ModelEntryWriterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
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

    [Fact]
    public void WriteRole_SameModelWithoutModalities_PreservesHandSetModalities()
    {
        // On-disk shape: an operator-set InputModalities override.
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "ContextWindow": 262144, "InputModalities": "Text, Image" } }
            """);

        // Re-set the same model with only a context-window change; no modality intent supplied.
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Manual,
            contextWindow: 131072, ModalityOverride.Unset, ModalityOverride.Unset, discovered: null);

        var entry = (Dictionary<string, object>)models["Main"];
        Assert.Equal("Text, Image", entry["InputModalities"]); // preserved (#1127)
        Assert.Equal(131072, entry["ContextWindow"]);          // explicit override applied
    }

    [Fact]
    public void WriteRole_SameModel_PreservesExistingClampOverDiscoveredWindow()
    {
        // Operator clamped ContextWindow below what the provider reports.
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "ContextWindow": 32000 } }
            """);

        // Re-select the same model (picker path): no explicit --context-window, but the probe
        // reports the model's full window. The operator's clamp must win (#1610): ContextWindow
        // is documented to take precedence over provider-reported detection.
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            contextWindow: null, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000));

        var entry = (Dictionary<string, object>)models["Main"];
        Assert.Equal(32000, entry["ContextWindow"]);
    }

    [Fact]
    public void WriteRole_FirstTimeSet_UsesDiscoveredWindow()
    {
        // No existing entry for the role: discovery is the fallback that seeds the value.
        var models = new Dictionary<string, object>();

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            contextWindow: null, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000));

        var entry = (Dictionary<string, object>)models["Main"];
        Assert.Equal(128000, entry["ContextWindow"]);
    }

    [Fact]
    public void WriteRole_SameModelFreshProbe_DoesNotOverrideExistingModalities()
    {
        // Operator override on disk; a fresh probe reports a coarser Text-only capability.
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "InputModalities": "Text, Image" } }
            """);

        // The stored override wins — discovery never silently overwrites it (#1610 / #5). This is
        // the same rule as ContextWindow: the field is documented to bypass automated detection.
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            contextWindow: null, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(input: ModelModality.Text, output: ModelModality.Text));

        var entry = (Dictionary<string, object>)models["Main"];
        Assert.Equal("Text, Image", entry["InputModalities"]);
    }

    [Fact]
    public void WriteRole_ExplicitModalityOverride_ReplacesExisting()
    {
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "InputModalities": "Text, Image" } }
            """);

        // Explicit operator input (--input-modalities Text) is the authority: it replaces the
        // stored override so the operator can actually change a value discovery no longer touches.
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Manual,
            contextWindow: null,
            ModalityOverride.Set(ModelModality.Text), ModalityOverride.Unset, discovered: null);

        var entry = (Dictionary<string, object>)models["Main"];
        Assert.Equal("Text", entry["InputModalities"]);
    }

    [Fact]
    public void WriteRole_ClearModalities_RemovesOverrideEvenWhenProbeReportsOne()
    {
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "InputModalities": "Text, Image", "OutputModalities": "Text" } }
            """);

        // --clear-modalities removes both overrides so runtime detection resolves them; clear
        // wins over BOTH the stored value and a probe that still reports modalities (#1610 / #4).
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Manual,
            contextWindow: null, ModalityOverride.Clear, ModalityOverride.Clear,
            Discovered(input: ModelModality.Text | ModelModality.Image));

        var entry = (Dictionary<string, object>)models["Main"];
        Assert.False(entry.ContainsKey("InputModalities"));
        Assert.False(entry.ContainsKey("OutputModalities"));
    }

    [Fact]
    public void WriteRole_ExistingEntryOmitsModelId_DoesNotFalseMatchDefaultModel()
    {
        // The entry omits ModelId; ModelReference would default it to the stock "qwen3:30b".
        // Setting the stock default model must NOT treat this as the same model and carry the
        // stray modalities onto a model the entry never named (#1610).
        var models = Models(
            """
            { "Main": { "Provider": "local-ollama", "InputModalities": "Text, Image" } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "local-ollama", "qwen3:30b", ModelDiscoverySource.Manual,
            contextWindow: null, ModalityOverride.Unset, ModalityOverride.Unset, discovered: null);

        var entry = (Dictionary<string, object>)models["Main"];
        Assert.Equal("qwen3:30b", entry["ModelId"]);
        Assert.False(entry.ContainsKey("InputModalities")); // stray modality dropped, not carried over
    }

    [Fact]
    public void WriteRole_SameModelManualReSet_PreservesDiscoveredProvenance()
    {
        // The model ID was originally resolved Live. A --context-window-only re-set (no probe,
        // so the caller passes Manual) must not relabel the ID's origin as Manual (#1610).
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "Provenance": "Live", "ContextWindow": 262144 } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Manual,
            contextWindow: 131072, ModalityOverride.Unset, ModalityOverride.Unset, discovered: null);

        var entry = (Dictionary<string, object>)models["Main"];
        Assert.Equal("Live", entry["Provenance"]); // origin preserved, not downgraded to Manual
        Assert.Equal(131072, entry["ContextWindow"]);
    }

    [Fact]
    public void WriteRole_SameModelFreshDiscovery_UpdatesProvenance()
    {
        // A fresh probe legitimately re-resolves the ID, so a Live discovery updates a stale
        // Defaults origin rather than being pinned to it.
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "Provenance": "Defaults" } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            contextWindow: null, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000));

        var entry = (Dictionary<string, object>)models["Main"];
        Assert.Equal("Live", entry["Provenance"]);
    }

    [Fact]
    public void WriteRole_CorruptExistingEntry_OverwritesInsteadOfThrowing()
    {
        // A legacy/hand-corrupted entry: "Vision" is not a valid ModelModality enum name, so
        // deserializing it throws JsonException. `model set` must still succeed and repair it
        // (#1610) rather than aborting on an entry it is about to overwrite.
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "InputModalities": "Vision" } }
            """);

        var ex = Record.Exception(() => ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            contextWindow: null, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000)));

        Assert.Null(ex);
        var entry = (Dictionary<string, object>)models["Main"];
        Assert.Equal("qwen-vl", entry["ModelId"]);
        Assert.Equal(128000, entry["ContextWindow"]);          // clean rewrite, nothing preserved
        Assert.False(entry.ContainsKey("InputModalities"));    // corrupt value dropped, not frozen
    }

    [Fact]
    public void WriteRole_DifferentModel_DropsPreviousModelModalities()
    {
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "InputModalities": "Text, Image" } }
            """);

        // Switching to a DIFFERENT model must not carry the old model's modalities over.
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "other-model", ModelDiscoverySource.Manual,
            contextWindow: null, ModalityOverride.Unset, ModalityOverride.Unset, discovered: null);

        var entry = (Dictionary<string, object>)models["Main"];
        Assert.Equal("other-model", entry["ModelId"]);
        Assert.False(entry.ContainsKey("InputModalities"));
    }

    private static Dictionary<string, object> Models(string json)
        => JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

    private static DiscoveredModel Discovered(
        int? contextWindow = null, ModelModality? input = null, ModelModality? output = null)
        => new()
        {
            ModelId = new ModelId("qwen-vl"),
            ContextWindowTokens = contextWindow,
            InputModalities = input,
            OutputModalities = output,
        };
}

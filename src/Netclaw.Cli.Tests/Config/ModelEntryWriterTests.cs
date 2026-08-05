// -----------------------------------------------------------------------
// <copyright file="ModelEntryWriterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;
using ModalityOverride = Netclaw.Cli.Config.ValueOverride<Netclaw.Configuration.ModelModality>;
using ContextWindowOverride = Netclaw.Cli.Config.ValueOverride<int>;

namespace Netclaw.Cli.Tests.Config;

/// <summary>
/// Guards the single persist path shared by `model set`, the init wizard, and the TUI
/// model manager. The writer persists capability values only after explicit operator input.
/// Runtime detection owns provider capability data (#1756).
/// Operator overrides survive model re-selection (#1127 and #1610).
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
            ContextWindowOverride.Set(131072), ModalityOverride.Unset, ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.Equal("Text, Image", entry["InputModalities"]); // preserved (#1127)
        Assert.Equal(131072, entry["ContextWindow"]);          // explicit override applied
    }

    [Fact]
    public void WriteRole_SameModel_PreservesExistingClampWithoutExplicitChange()
    {
        // Operator clamped ContextWindow below what the provider reports.
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "ContextWindow": 32000 } }
            """);

        // Re-select the same model through a picker that has no override input.
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.Equal(32000, entry["ContextWindow"]);
    }

    [Fact]
    public void WriteRole_FirstTimeSet_OmitsCapabilityOverrides()
    {
        var models = new Dictionary<string, object>();

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.False(entry.ContainsKey("ContextWindow"));
        Assert.False(entry.ContainsKey("InputModalities"));
        Assert.False(entry.ContainsKey("OutputModalities"));
    }

    [Fact]
    public void WriteRole_ProviderCapabilityChange_ReachesRuntimeResolution()
    {
        var models = new Dictionary<string, object>();
        var selectedDuringProbe = new DiscoveredModel
        {
            ModelId = new ModelId("deepseek-v4-flash-dspark"),
            ContextWindowTokens = 327680,
            InputModalities = ModelModality.Text,
            OutputModalities = ModelModality.Text,
        };

        // The writer receives the selected identity, but it does not receive dynamic capabilities.
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", selectedDuringProbe.ModelId.Value, ModelDiscoverySource.Live,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        var main = ConfigFileHelper.DeserializeSection<ModelReference>(ActiveEntry(models, "Main"))!;
        var detectedAtStartup = new ResolvedModelCapabilities(
            main.ModelId,
            ModelModality.Text | ModelModality.Image,
            ModelModality.Text,
            100000);

        var resolved = ModelCapabilityResolution.ResolveModelCapabilities(
            new ModelSelection { Main = main }, detectedAtStartup);

        Assert.Equal(100000, resolved.ContextWindowTokens);
        Assert.Equal(ModelModality.Text | ModelModality.Image, resolved.InputModalities);
        Assert.Equal(ModelModality.Text, resolved.OutputModalities);
    }

    [Fact]
    public void WriteRole_ClearContextWindow_DropsStoredClamp()
    {
        // The operator removes the clamp. Runtime detection resolves the window.
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "ContextWindow": 32000 } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            ContextWindowOverride.Clear, ModalityOverride.Unset, ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.False(entry.ContainsKey("ContextWindow")); // clamp removed → runtime detection
    }

    [Fact]
    public void WriteRole_SameDefinitionWithoutWindow_DiscoveryDoesNotResurrect()
    {
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl" } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        Assert.False(ActiveEntry(models, "Main").ContainsKey("ContextWindow"));
    }

    [Fact]
    public void WriteRole_SameModel_PreservesExistingModalities()
    {
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "InputModalities": "Text, Image" } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.Equal("Text, Image", entry["InputModalities"]);
    }

    [Fact]
    public void WriteRole_SameModelEntryClearedModality_DiscoveryDoesNotResurrect()
    {
        // The entry exists for this model but carries NO modality — the on-disk shape left behind
        // by a prior --clear-modalities. A later same-model re-set that carries a probe result must
        // NOT re-add the discovered modality, or it would silently undo the operator's clear and
        // re-demote a multimodal-misreporting model on next boot (#1610).
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl" } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.False(entry.ContainsKey("InputModalities")); // stays cleared, not resurrected
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
            ContextWindowOverride.Unset,
            ModalityOverride.Set(ModelModality.Text), ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.Equal("Text", entry["InputModalities"]);
    }

    [Fact]
    public void WriteRole_ClearModalities_RemovesOverrideEvenWhenProbeReportsOne()
    {
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "InputModalities": "Text, Image", "OutputModalities": "Text" } }
            """);

        // The command removes both overrides. Runtime detection resolves the modalities.
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Manual,
            ContextWindowOverride.Unset, ModalityOverride.Clear, ModalityOverride.Clear);

        var entry = ActiveEntry(models, "Main");
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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.Equal("qwen3:30b", entry["ModelId"]);
        Assert.False(entry.ContainsKey("InputModalities")); // stray modality dropped, not carried over
    }

    [Theory]
    [InlineData(ModelDiscoverySource.Live, ModelDiscoverySource.Manual, ModelDiscoverySource.Live)]
    [InlineData(ModelDiscoverySource.Live, ModelDiscoverySource.Defaults, ModelDiscoverySource.Live)]
    [InlineData(ModelDiscoverySource.Defaults, ModelDiscoverySource.Live, ModelDiscoverySource.Live)]
    public void WriteRole_SameModel_PreservesProvenanceUnlessFreshlyDiscovered(
        ModelDiscoverySource existing,
        ModelDiscoverySource incoming,
        ModelDiscoverySource expected)
    {
        var models = Models(
            $$"""
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "Provenance": "{{existing}}" } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", incoming,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.Equal(expected.ToString(), entry["Provenance"]);
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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset));

        Assert.Null(ex);
        var entry = ActiveEntry(models, "Main");
        Assert.Equal("qwen-vl", entry["ModelId"]);
        Assert.False(entry.ContainsKey("ContextWindow"));      // existing absence stays runtime detection
        Assert.False(entry.ContainsKey("InputModalities"));    // corrupt value dropped, not frozen
    }

    [Fact]
    public void WriteRole_CorruptModalityButValidWindow_PreservesWindow()
    {
        // A hand-edited entry with a VALID ContextWindow clamp but an unparseable InputModalities
        // string. Discarding the whole entry (the old catch behavior) would silently clobber the
        // operator's still-valid window; the field-tolerant read must preserve it (#1610).
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "ContextWindow": 32768, "InputModalities": "text_and_image" } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.Equal(32768, entry["ContextWindow"]);         // operator clamp preserved, not clobbered
        Assert.False(entry.ContainsKey("InputModalities"));  // unparseable override dropped
    }

    [Fact]
    public void WriteRole_CorruptEntryForDifferentModel_DoesNotLeakWindow()
    {
        // A corrupt entry that names a DIFFERENT model must not have its ContextWindow carried
        // onto the model being set — the resilient read only preserves a same-model entry.
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "other-model", "ContextWindow": 32768, "InputModalities": "Vision" } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        var entry = ActiveEntry(models, "Main");
        Assert.Equal("qwen-vl", entry["ModelId"]);
        Assert.False(entry.ContainsKey("ContextWindow"));
    }

    [Fact]
    public void WriteRole_SwitchAwayAndBack_PreservesPreviousModelModalities()
    {
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "InputModalities": "Text, Image" } }
            """);

        // Switching roles changes only the role reference. The old definition remains intact.
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "other-model", ModelDiscoverySource.Manual,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        Assert.Equal("other-model", ActiveEntry(models, "Main")["ModelId"]);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Manual,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset);

        Assert.Equal("Text, Image", ActiveEntry(models, "Main")["InputModalities"]);
    }

    private static Dictionary<string, object> Models(string json)
        => JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

    private static Dictionary<string, object> ActiveEntry(
        Dictionary<string, object> models, string role)
    {
        var roles = (Dictionary<string, object>)models["Roles"];
        var definitionName = (string)roles[role];
        var definitions = (Dictionary<string, object>)models["Definitions"];
        return (Dictionary<string, object>)definitions[definitionName];
    }

}

// -----------------------------------------------------------------------
// <copyright file="ModelEntryWriterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Xunit;
using ModalityOverride = Netclaw.Cli.Config.ValueOverride<Netclaw.Configuration.ModelModality>;
using ContextWindowOverride = Netclaw.Cli.Config.ValueOverride<int>;

namespace Netclaw.Cli.Tests.Config;

/// <summary>
/// Guards the single persist path shared by `model set`, the init wizard, and the TUI
/// model manager. The rule under test: modalities are written only when the discovery
/// source genuinely reported them, so an unknown is never frozen into config as a
/// permanent "Text" override (#1290), and operator-owned overrides survive re-selection
/// (#1127 / #1610).
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
            ContextWindowOverride.Set(131072), ModalityOverride.Unset, ModalityOverride.Unset, discovered: null);

        var entry = ActiveEntry(models, "Main");
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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000));

        var entry = ActiveEntry(models, "Main");
        Assert.Equal(32000, entry["ContextWindow"]);
    }

    [Fact]
    public void WriteRole_FirstTimeSet_UsesDiscoveredWindow()
    {
        // No existing entry for the role: discovery is the fallback that seeds the value.
        var models = new Dictionary<string, object>();

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000));

        var entry = ActiveEntry(models, "Main");
        Assert.Equal(128000, entry["ContextWindow"]);
    }

    [Fact]
    public void WriteRole_ClearContextWindow_DropsStoredClampAndDiscoveredWindow()
    {
        // Operator clamped the window; now --clear-context-window removes the clamp so runtime
        // detection resolves it. Clear wins over BOTH the stored value and a probe that reports
        // a window (#1610) — mirroring --clear-modalities.
        var models = Models(
            """
            { "Main": { "Provider": "spark", "ModelId": "qwen-vl", "ContextWindow": 32000 } }
            """);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Live,
            ContextWindowOverride.Clear, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000));

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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000));

        Assert.False(ActiveEntry(models, "Main").ContainsKey("ContextWindow"));
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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(input: ModelModality.Text, output: ModelModality.Text));

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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(input: ModelModality.Text | ModelModality.Image));

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
            ModalityOverride.Set(ModelModality.Text), ModalityOverride.Unset, discovered: null);

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

        // --clear-modalities removes both overrides so runtime detection resolves them; clear
        // wins over BOTH the stored value and a probe that still reports modalities (#1610 / #4).
        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Manual,
            ContextWindowOverride.Unset, ModalityOverride.Clear, ModalityOverride.Clear,
            Discovered(input: ModelModality.Text | ModelModality.Image));

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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset, discovered: null);

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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset, discovered: null);

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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000)));

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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000));

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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset,
            Discovered(contextWindow: 128000));

        var entry = ActiveEntry(models, "Main");
        Assert.Equal("qwen-vl", entry["ModelId"]);
        Assert.Equal(128000, entry["ContextWindow"]);  // discovered window, not the other model's 32768
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
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset, discovered: null);

        Assert.Equal("other-model", ActiveEntry(models, "Main")["ModelId"]);

        ModelEntryWriter.WriteRole(
            models, "Main", "spark", "qwen-vl", ModelDiscoverySource.Manual,
            ContextWindowOverride.Unset, ModalityOverride.Unset, ModalityOverride.Unset, discovered: null);

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

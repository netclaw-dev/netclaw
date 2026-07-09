// -----------------------------------------------------------------------
// <copyright file="ModelEntryWriter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Config;

/// <summary>
/// The single place a model role assignment is assembled for persistence to the
/// "Models" section of netclaw.json. The CLI (<c>netclaw model set</c>), the init
/// wizard, and the TUI model manager all route through here so their on-disk shape
/// cannot drift apart — each used to hand-roll its own dictionary, which is how the
/// same modality bug landed in three places (#1290).
/// </summary>
internal static class ModelEntryWriter
{
    /// <summary>
    /// Write a role's model entry into <paramref name="modelsSection"/> non-destructively.
    /// Two on-disk attributes are treated as operator-owned overrides that provider discovery
    /// must never silently clobber on a same-<c>(provider, modelId)</c> re-set:
    /// <list type="bullet">
    /// <item><description>
    /// <c>ContextWindow</c> and the modalities are documented to "take precedence over
    /// provider-reported capability detection". So the precedence for each is:
    /// explicit operator input (this call) &gt; existing stored value &gt; probe. A fresh probe
    /// tops up a first-time set or a model switch, but never overwrites a value already on disk
    /// — that overwrite was the #1127 loss (a re-set wiped a hand-set modality) and its
    /// context-window twin (#1610).
    /// </description></item>
    /// </list>
    /// Because the stored value now wins, the operator needs a way to *change* it: an explicit
    /// <see cref="ModalityOverride.Set"/> replaces it and <see cref="ModalityOverride.Clear"/>
    /// removes it (falling back to runtime detection). Switching a role to a <em>different</em>
    /// model does not carry the old model's attributes over — they belonged to that model.
    /// </summary>
    /// <param name="contextWindow">
    /// The explicit operator override (<c>--context-window</c>). When supplied it wins over
    /// everything; the picker, which has no such input, passes null.
    /// </param>
    /// <param name="inputModalities">Operator intent for input modalities (set / clear / unset).</param>
    /// <param name="outputModalities">Operator intent for output modalities (set / clear / unset).</param>
    /// <param name="discovered">
    /// The probe result, if a probe ran. Its context window and modalities seed a first-time set
    /// or a model switch only; an existing stored value wins over them.
    /// </param>
    internal static void WriteRole(
        Dictionary<string, object> modelsSection,
        string roleKey,
        string provider,
        string? modelId,
        ModelDiscoverySource? provenance,
        int? contextWindow,
        ModalityOverride inputModalities,
        ModalityOverride outputModalities,
        DiscoveredModel? discovered)
    {
        var existing = ReadSameModelEntry(modelsSection, roleKey, provider, modelId);

        // Provenance records how the model ID was resolved, not how the entry was last
        // edited. A same-model re-set that did not itself resolve the ID (no probe → the
        // caller passes Manual) must not downgrade a previously discovered origin
        // (Live/Defaults) to Manual; only a fresh discovery updates it (#1610).
        if (existing?.Provenance is { } priorProvenance && provenance == ModelDiscoverySource.Manual)
            provenance = priorProvenance;

        // Precedence for every operator-owned attribute: explicit input > existing stored value
        // > probe. Collapsing the explicit and discovered values in the caller defeated
        // preservation, because the probe/picker paths always pass a discovered value, so the
        // stored value was overwritten on every re-selection.
        contextWindow ??= existing?.ContextWindow ?? discovered?.ContextWindowTokens;
        var resolvedInput = ResolveModality(inputModalities, existing?.InputModalities, discovered?.InputModalities);
        var resolvedOutput = ResolveModality(outputModalities, existing?.OutputModalities, discovered?.OutputModalities);

        modelsSection[roleKey] = BuildModelEntry(
            provider, modelId, provenance, contextWindow, resolvedInput, resolvedOutput);
    }

    /// <summary>
    /// Resolves the modality actually written: an explicit operator set or clear wins outright
    /// (the operator is the authority on a manual override); otherwise an existing stored value
    /// is preserved, and provider discovery only fills a genuine gap (first set / model switch).
    /// </summary>
    private static ModelModality? ResolveModality(
        ModalityOverride @override, ModelModality? existing, ModelModality? discovered)
        => @override.Supplied
            ? @override.Value            // Set(value) → value; Clear → null (key omitted downstream)
            : existing ?? discovered;    // preserve on-disk value; discovery only seeds a gap

    /// <summary>
    /// The role's current entry, but only when it already references the same
    /// <c>(provider, modelId)</c>; null when the role is unset or points at a different
    /// model (whose attributes must not carry over to the newly-set one).
    /// </summary>
    private static ModelReference? ReadSameModelEntry(
        Dictionary<string, object> modelsSection, string roleKey, string provider, string? modelId)
    {
        if (!modelsSection.TryGetValue(roleKey, out var raw) || raw is null)
            return null;

        // ModelReference defaults Provider/ModelId to the stock local-ollama model, so an
        // entry that OMITS either key deserializes to that default and would false-match a
        // re-set of the stock model — carrying stray attributes onto a model the entry never
        // actually named. Only treat the entry as the same model when it explicitly declares
        // both keys (#1610).
        if (!RawContainsProperty(raw, nameof(ModelReference.Provider))
            || !RawContainsProperty(raw, nameof(ModelReference.ModelId)))
            return null;

        // A legacy or hand-corrupted entry (e.g. an unrecognized modality enum string, or a
        // shape that predates a schema change) must not abort `model set`: we are about to
        // overwrite this role anyway, and before the non-destructive rewrite existed the
        // command simply clobbered it. Degrade an unreadable existing entry to "nothing to
        // preserve" so the operator can still repair it, rather than throwing JsonException
        // out of a write they explicitly requested.
        ModelReference? existing;
        try
        {
            existing = ConfigFileHelper.DeserializeSection<ModelReference>(raw);
        }
        catch (JsonException)
        {
            return null;
        }

        if (existing is null)
            return null;

        return string.Equals(existing.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ModelId, modelId, StringComparison.OrdinalIgnoreCase)
            ? existing
            : null;
    }

    /// <summary>
    /// Whether the raw config value explicitly declares <paramref name="propertyName"/>. The
    /// value is either a <see cref="JsonElement"/> (loaded from disk) or an in-memory
    /// dictionary (rewritten this run) — the two shapes <see cref="ConfigFileHelper.DeserializeSection{T}"/>
    /// accepts. The match is case-insensitive to mirror the case-insensitive deserialization.
    /// </summary>
    private static bool RawContainsProperty(object raw, string propertyName)
    {
        switch (raw)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            case IDictionary<string, object> dict:
                foreach (var key in dict.Keys)
                {
                    if (string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Builds the dictionary written under <c>Models[role]</c>.
    /// </summary>
    /// <remarks>
    /// Modalities are written ONLY when the discovery source genuinely reported them
    /// (non-null). A null modality means "the provider did not say" — it is
    /// deliberately omitted so the daemon's capability detection resolves it at
    /// runtime. Writing a guessed <see cref="ModelModality.Text"/> here would bake a
    /// permanent override into config that beats real detection on every boot, which
    /// is exactly what silently demoted multimodal self-hosted models to text-only.
    /// </remarks>
    internal static Dictionary<string, object> BuildModelEntry(
        string provider,
        string? modelId,
        ModelDiscoverySource? provenance,
        int? contextWindow,
        ModelModality? inputModalities,
        ModelModality? outputModalities)
    {
        var entry = new Dictionary<string, object> { ["Provider"] = provider };

        if (!string.IsNullOrWhiteSpace(modelId))
            entry["ModelId"] = modelId;

        if (provenance is { } prov)
            entry["Provenance"] = prov.ToString();

        if (contextWindow is { } window)
            entry["ContextWindow"] = window;

        if (inputModalities is { } input)
            entry["InputModalities"] = input.ToString();

        if (outputModalities is { } output)
            entry["OutputModalities"] = output.ToString();

        return entry;
    }
}

/// <summary>
/// An operator's intent for a modality field on <c>model set</c>. A plain
/// <see cref="ModelModality"/>? cannot express it, because two of the three states both
/// resolve to null yet behave oppositely: "not supplied" must preserve any existing override,
/// while "clear" must win over it. The tri-state is: <see cref="Unset"/> (leave it to the
/// stored value / discovery), <see cref="Set"/> (replace with an explicit value), and
/// <see cref="Clear"/> (remove the override so runtime detection resolves it).
/// </summary>
internal readonly record struct ModalityOverride(bool Supplied, ModelModality? Value)
{
    /// <summary>Operator said nothing — preserve the stored value, else fall back to discovery.</summary>
    internal static ModalityOverride Unset => default;

    /// <summary>Operator asked to remove the override so runtime capability detection resolves it.</summary>
    internal static ModalityOverride Clear => new(true, null);

    /// <summary>Operator supplied an explicit modality set that replaces whatever is stored.</summary>
    internal static ModalityOverride Set(ModelModality value) => new(true, value);
}

// -----------------------------------------------------------------------
// <copyright file="ModelEntryWriter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    /// Write a role's model entry into <paramref name="modelsSection"/>
    /// non-destructively. When the role already points at the SAME
    /// <c>(provider, modelId)</c>, operator-set attributes the caller did not supply are
    /// preserved instead of dropped — chiefly the modalities, which have no CLI input
    /// and can only be hand-edited, so rebuilding the entry from scratch is exactly how
    /// a re-set or a context-window tweak silently deleted a hand-set
    /// <c>InputModalities</c> (#1127). Switching a role to a <em>different</em> model
    /// does not carry the old model's attributes over — they belonged to that model.
    /// </summary>
    internal static void WriteRole(
        Dictionary<string, object> modelsSection,
        string roleKey,
        string provider,
        string? modelId,
        ModelDiscoverySource? provenance,
        int? contextWindow,
        ModelModality? inputModalities,
        ModelModality? outputModalities)
    {
        var existing = ReadSameModelEntry(modelsSection, roleKey, provider, modelId);
        if (existing is not null)
        {
            contextWindow ??= existing.ContextWindow;
            inputModalities ??= existing.InputModalities;
            outputModalities ??= existing.OutputModalities;
        }

        modelsSection[roleKey] = BuildModelEntry(
            provider, modelId, provenance, contextWindow, inputModalities, outputModalities);
    }

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

        var existing = ConfigFileHelper.DeserializeSection<ModelReference>(raw);
        if (existing is null)
            return null;

        return string.Equals(existing.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ModelId, modelId, StringComparison.OrdinalIgnoreCase)
            ? existing
            : null;
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

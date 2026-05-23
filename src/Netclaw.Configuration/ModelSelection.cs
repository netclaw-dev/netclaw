// -----------------------------------------------------------------------
// <copyright file="ModelSelection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Named model roles bound from the "Models" configuration section.
/// Each role points to a <see cref="ModelReference"/> identifying
/// which provider and model to use for that purpose.
/// </summary>
public sealed class ModelSelection
{
    /// <summary>Primary model for all interactions.</summary>
    public ModelReference Main { get; set; } = new();

    /// <summary>Automatic failover model. Falls back to Main if not set.</summary>
    public ModelReference? Fallback { get; set; }

    /// <summary>Cheaper/faster model for compaction. Falls back to Main if not set.</summary>
    public ModelReference? Compaction { get; set; }

    /// <summary>
    /// Optional map of operator overrides keyed by <c>"{provider}/{modelId}"</c>.
    /// Merged into matching role records by <see cref="ApplyCatalogOverlays"/>;
    /// inline values on a role win over the catalog entry. Typically empty —
    /// auto-detection covers most operators' setups and only persists when an
    /// override is explicitly set (e.g. a context-window cap or a forced
    /// modality).
    /// </summary>
    public Dictionary<string, ModelOverride>? Catalog { get; set; }

    /// <summary>
    /// Builds the catalog key for a (provider, modelId) pair. Provider keys
    /// are user-defined identifiers and conventionally slash-free; model ids
    /// may contain slashes (e.g. HF-shaped <c>org/model</c>), so the first
    /// slash is the only meaningful delimiter.
    /// </summary>
    public static string CatalogKey(string provider, string modelId) => $"{provider}/{modelId}";

    /// <summary>
    /// Folds <see cref="Catalog"/> entries into matching role records,
    /// preserving inline values (inline wins). Idempotent: a second call is
    /// a no-op because inline already mirrors the catalog after the first
    /// merge. Safe to call when <see cref="Catalog"/> is null.
    /// </summary>
    public void ApplyCatalogOverlays()
    {
        if (Catalog is null || Catalog.Count == 0)
            return;

        Merge(Main);
        if (Fallback is not null) Merge(Fallback);
        if (Compaction is not null) Merge(Compaction);
    }

    private void Merge(ModelReference role)
    {
        if (!Catalog!.TryGetValue(CatalogKey(role.Provider, role.ModelId), out var overlay))
            return;

        role.ContextWindow ??= overlay.ContextWindow;
        role.InputModalities ??= overlay.InputModalities;
        role.OutputModalities ??= overlay.OutputModalities;
    }
}

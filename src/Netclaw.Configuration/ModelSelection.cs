// -----------------------------------------------------------------------
// <copyright file="ModelSelection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

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
        var key = CatalogKey(role.Provider, role.ModelId);
        var overlay = FindOverlay(key);
        if (overlay is null) return;

        role.ContextWindow ??= overlay.ContextWindow;
        role.InputModalities ??= overlay.InputModalities;
        role.OutputModalities ??= overlay.OutputModalities;
    }

    private ModelOverride? FindOverlay(string key)
    {
        // Fast path: exact-case match against the operator-written key.
        if (Catalog!.TryGetValue(key, out var direct)) return direct;
        // Tolerate provider-name casing drift between role.Provider and the
        // catalog key (operator hand-edits, picker re-writes, the Providers
        // dictionary which is case-sensitive vs. ProviderRenamer which is
        // not). Catalog is typically empty or single-digit entries so the
        // O(n) scan is negligible.
        foreach (var kvp in Catalog)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return null;
    }

    /// <summary>
    /// Bind a <see cref="ModelSelection"/> from configuration, then re-load
    /// <see cref="Catalog"/> directly from the raw config file so dictionary
    /// keys containing <c>:</c> survive intact, and apply overlays. Use this
    /// instead of calling <c>Get&lt;ModelSelection&gt;()</c> directly.
    /// <para>
    /// Microsoft.Extensions.Configuration uses <c>:</c> as its hierarchical
    /// path separator and applies that splitting to dictionary keys too, so
    /// the binder turns <c>Catalog["local-ollama/qwen3:30b"]</c> into
    /// <c>Catalog["local-ollama/qwen3"]</c> with a stray <c>30b</c>
    /// sub-property — silently dropping the operator's override. Ollama's
    /// default ModelId is <c>qwen3:30b</c>, so the broken path is the most
    /// common one. Re-parsing the raw JSON via System.Text.Json (which
    /// treats keys as opaque strings) is the cleanest fix that preserves
    /// the rest of the M.E.Configuration pipeline for everything else.
    /// </para>
    /// </summary>
    public static ModelSelection LoadFromConfiguration(
        IConfiguration modelsSection, string netclawConfigPath)
    {
        var selection = modelsSection.Get<ModelSelection>() ?? new ModelSelection();
        selection.Catalog = LoadCatalogFromFile(netclawConfigPath) ?? selection.Catalog;
        selection.ApplyCatalogOverlays();
        return selection;
    }

    /// <summary>
    /// Parse <c>Models.Catalog</c> from <paramref name="netclawConfigPath"/>
    /// directly via System.Text.Json (bypassing
    /// Microsoft.Extensions.Configuration's path-splitting binder). Returns
    /// null when the file is absent, has no Models section, has no Catalog
    /// property, or Catalog is not a JSON object — callers can keep
    /// whatever binder-produced value they already have in that case.
    /// </summary>
    public static Dictionary<string, ModelOverride>? LoadCatalogFromFile(string netclawConfigPath)
    {
        if (!File.Exists(netclawConfigPath)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(netclawConfigPath));
        if (!doc.RootElement.TryGetProperty("Models", out var models)) return null;
        if (!models.TryGetProperty("Catalog", out var catalog)) return null;
        if (catalog.ValueKind != JsonValueKind.Object) return null;

        return JsonSerializer.Deserialize<Dictionary<string, ModelOverride>>(
            catalog.GetRawText(),
            CatalogJsonOptions);
    }

    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

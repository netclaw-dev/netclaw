// -----------------------------------------------------------------------
// <copyright file="ModelEntryWriter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Json;
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
    private static readonly string[] LegacyEnvironmentPrefixes =
    [
        "NETCLAW_Models__Main__",
        "NETCLAW_Models__Fallback__",
        "NETCLAW_Models__Compaction__",
    ];

    internal static string? FindLegacyEnvironmentOverride()
        => Environment.GetEnvironmentVariables().Keys
            .OfType<string>()
            .FirstOrDefault(key => LegacyEnvironmentPrefixes.Any(prefix =>
                key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    internal static bool MigrateLegacy(Dictionary<string, object> modelsSection)
    {
        if (modelsSection.ContainsKey("Definitions") || modelsSection.ContainsKey("Roles"))
            return false;
        if (!modelsSection.Keys.Any(key => key is "Main" or "Fallback" or "Compaction"))
            return false;
        EnsureNamedShape(modelsSection);
        return true;
    }

    internal static bool ClearRole(Dictionary<string, object> modelsSection, string roleKey)
    {
        var (_, roles) = EnsureNamedShape(modelsSection);
        return roles.Remove(roleKey);
    }

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
    /// <see cref="ValueOverride{T}.Set"/> replaces it and <see cref="ValueOverride{T}.Clear"/>
    /// removes it (falling back to runtime detection). Switching a role to a <em>different</em>
    /// model does not carry the old model's attributes over — they belonged to that model.
    /// </summary>
    /// <param name="contextWindow">
    /// Operator intent for the context window (set / clear / unset). <c>--context-window</c> sets
    /// it (wins over everything), <c>--clear-context-window</c> removes the clamp so detection
    /// resolves it, and the picker — which has no such input — passes <see cref="ValueOverride{T}.Unset"/>.
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
        ValueOverride<int> contextWindow,
        ValueOverride<ModelModality> inputModalities,
        ValueOverride<ModelModality> outputModalities,
        DiscoveredModel? discovered)
    {
        var (definitions, roles) = EnsureNamedShape(modelsSection, roleKey);
        var definitionName = FindDefinition(definitions, provider, modelId)
                             ?? CreateDefinitionName(definitions, provider, modelId);
        var existing = ReadSameModelEntry(definitions, definitionName, provider, modelId);

        // Provenance records how the model ID was resolved, not how the entry was last edited. A
        // same-model re-set that did not itself freshly resolve the ID — no probe (the caller
        // passes Manual) or a probe that failed (Defaults) — must not downgrade a previously
        // discovered origin. Only a fresh successful discovery (Live) re-stamps it (#1610).
        if (existing?.Provenance is { } priorProvenance && provenance != ModelDiscoverySource.Live)
            provenance = priorProvenance;

        // Precedence for every operator-owned attribute: explicit input > existing stored value
        // > probe. Collapsing the explicit and discovered values in the caller defeated
        // preservation, because the probe/picker paths always pass a discovered value, so the
        // stored value was overwritten on every re-selection.
        var sameModelEntry = existing is not null;
        var resolvedWindow = ResolveContextWindow(
            contextWindow, sameModelEntry, existing?.ContextWindow, discovered?.ContextWindowTokens);
        var resolvedInput = ResolveModality(inputModalities, sameModelEntry, existing?.InputModalities, discovered?.InputModalities);
        var resolvedOutput = ResolveModality(outputModalities, sameModelEntry, existing?.OutputModalities, discovered?.OutputModalities);

        definitions[definitionName] = BuildModelEntry(
            provider, modelId, provenance, resolvedWindow, resolvedInput, resolvedOutput);
        roles[roleKey] = definitionName;
    }

    private static (Dictionary<string, object> Definitions, Dictionary<string, object> Roles)
        EnsureNamedShape(Dictionary<string, object> modelsSection, string? overwrittenRole = null)
    {
        var hasNamed = modelsSection.ContainsKey("Definitions") || modelsSection.ContainsKey("Roles");
        var hasLegacy = modelsSection.Keys.Any(key =>
            key is "Main" or "Fallback" or "Compaction");

        if (hasNamed && hasLegacy)
            throw new InvalidOperationException("Models configuration mixes legacy roles with Definitions/Roles.");

        if (hasNamed)
        {
            if (!modelsSection.ContainsKey("Definitions") || !modelsSection.ContainsKey("Roles"))
                throw new InvalidOperationException("Named Models configuration requires Definitions and Roles.");

            return (GetDictionary(modelsSection, "Definitions"), GetDictionary(modelsSection, "Roles"));
        }

        var definitions = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var roles = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in new[] { "Main", "Fallback", "Compaction" })
        {
            if (!modelsSection.TryGetValue(role, out var raw) || raw is null)
                continue;

            if (!RawContainsProperty(raw, nameof(ModelReference.Provider))
                || !RawContainsProperty(raw, nameof(ModelReference.ModelId)))
            {
                if (string.Equals(role, overwrittenRole, StringComparison.OrdinalIgnoreCase))
                    continue;
                throw new InvalidOperationException(
                    $"Models:{role} must explicitly declare Provider and ModelId before migration.");
            }

            ModelReference model;
            try
            {
                model = ConfigFileHelper.DeserializeSection<ModelReference>(raw)
                        ?? throw new InvalidOperationException($"Models:{role} could not be parsed.");
            }
            catch (JsonException)
            {
                model = ReadLegacyIdentity(raw)
                        ?? throw new InvalidOperationException($"Models:{role} could not be repaired.");
            }
            var existingName = FindDefinition(definitions, model.Provider, model.ModelId);
            if (existingName is not null)
            {
                var existing = ConfigFileHelper.DeserializeSection<ModelReference>(definitions[existingName])!;
                if (!Equivalent(existing, model))
                {
                    throw new InvalidOperationException(
                        $"Legacy model roles conflict for {model.Provider}/{model.ModelId}; " +
                        $"align their metadata before migration.");
                }

                roles[role] = existingName;
                continue;
            }

            var name = CreateDefinitionName(definitions, model.Provider, model.ModelId);
            definitions[name] = BuildModelEntry(
                model.Provider, model.ModelId, model.Provenance, model.ContextWindow,
                model.InputModalities, model.OutputModalities);
            roles[role] = name;
        }

        modelsSection.Clear();
        modelsSection["Definitions"] = definitions;
        modelsSection["Roles"] = roles;
        return (definitions, roles);
    }

    private static ModelReference? ReadLegacyIdentity(object raw)
    {
        var json = raw is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(raw, JsonDefaults.ConfigFile);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetString(root, nameof(ModelReference.Provider), out var provider)
            || !TryGetString(root, nameof(ModelReference.ModelId), out var modelId))
            return null;

        var model = new ModelReference { Provider = provider, ModelId = modelId };
        if (TryGetInt32(root, nameof(ModelReference.ContextWindow), out var contextWindow))
            model.ContextWindow = contextWindow;
        return model;
    }

    private static Dictionary<string, object> GetDictionary(Dictionary<string, object> parent, string key)
    {
        var raw = parent[key];
        if (raw is Dictionary<string, object> dictionary)
            return dictionary;
        if (raw is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            dictionary = JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText()) ?? [];
            parent[key] = dictionary;
            return dictionary;
        }

        throw new InvalidOperationException($"Models:{key} must be an object.");
    }

    private static string? FindDefinition(
        Dictionary<string, object> definitions, string provider, string? modelId)
    {
        foreach (var (name, raw) in definitions)
        {
            ModelReference? model;
            try
            {
                model = ConfigFileHelper.DeserializeSection<ModelReference>(raw);
            }
            catch (JsonException)
            {
                model = ReadLegacyIdentity(raw);
            }

            if (model is not null
                && string.Equals(model.Provider, provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return null;
    }

    private static string CreateDefinitionName(
        Dictionary<string, object> definitions, string provider, string? modelId)
    {
        var raw = $"{provider}-{modelId}";
        var stem = string.Join('-', raw.ToLowerInvariant()
            .Split(['/', ':', '.', '_', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Trim('-');
        if (string.IsNullOrWhiteSpace(stem))
            stem = "model";

        var candidate = stem;
        for (var suffix = 2; definitions.ContainsKey(candidate); suffix++)
            candidate = $"{stem}-{suffix}";
        return candidate;
    }

    private static bool Equivalent(ModelReference left, ModelReference right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.ModelId, right.ModelId, StringComparison.OrdinalIgnoreCase)
           && left.ContextWindow == right.ContextWindow
           && left.Provenance == right.Provenance
           && left.InputModalities == right.InputModalities
           && left.OutputModalities == right.OutputModalities;

    /// <summary>
    /// Resolves the modality actually written. An explicit operator set or clear wins outright
    /// (the operator is the authority on a manual override). Otherwise, when the same model already
    /// has an entry on disk, that entry is honored verbatim — including a deliberately-cleared
    /// (absent) modality, so a later probe cannot silently resurrect an override the operator
    /// removed with <c>--clear-modalities</c> (#1610). Provider discovery only seeds a genuine gap:
    /// a first-time set or a switch to a different model, where no entry exists yet.
    /// </summary>
    private static ModelModality? ResolveModality(
        ValueOverride<ModelModality> @override, bool sameModelEntryExists,
        ModelModality? existing, ModelModality? discovered)
        => @override.Supplied
            ? @override.Value                 // Set(value) → value; Clear → null (key omitted downstream)
            : sameModelEntryExists ? existing  // same model on disk: honor it, incl. a cleared (null) value
                : discovered;                  // first set / model switch: seed from discovery

    /// <summary>
    /// Resolves the context window actually written. Mirrors <see cref="ResolveModality"/> for the
    /// explicit cases: <c>--context-window</c> (Set) or <c>--clear-context-window</c> (Clear) wins,
    /// otherwise an existing definition is honored verbatim, including absence. Discovery seeds
    /// only a new definition. This keeps manual JSON edits and explicit clears stable without a
    /// hidden tombstone representation.
    /// </summary>
    private static int? ResolveContextWindow(
        ValueOverride<int> @override, bool sameModelEntryExists, int? existing, int? discovered)
        => @override.Supplied
            ? @override.Value            // Set(n) → n; Clear → null (drop the clamp → runtime detects)
            : sameModelEntryExists ? existing : discovered;

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
        // command simply clobbered it.
        ModelReference? existing;
        try
        {
            existing = ConfigFileHelper.DeserializeSection<ModelReference>(raw);
        }
        catch (JsonException)
        {
            // Strict deserialization failed — in practice a stale/unknown modality or provenance
            // enum string, which JsonStringEnumConverter rejects by throwing. Discarding the whole
            // entry here would silently clobber a still-valid operator-owned ContextWindow (#1610),
            // so recover the throw-proof fields and drop only the unparseable overrides.
            return ReadResilient(raw, provider, modelId);
        }

        if (existing is null)
            return null;

        return string.Equals(existing.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ModelId, modelId, StringComparison.OrdinalIgnoreCase)
            ? existing
            : null;
    }

    /// <summary>
    /// Recovers the preservation-worthy fields from an entry that failed strict deserialization.
    /// Provider, ModelId and ContextWindow are plain strings/int that never throw, so they are read
    /// directly; the modality/provenance overrides — the fields that failed to parse — are dropped
    /// (left null) because a value we could not read must not be preserved. Returns null when the
    /// recovered entry names a different model (its attributes must not carry over) or is not a
    /// JSON object.
    /// </summary>
    private static ModelReference? ReadResilient(object raw, string provider, string? modelId)
    {
        var json = raw is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(raw, JsonDefaults.ConfigFile);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        // Confirm the poisoned entry still names the model being set before preserving anything.
        if (!TryGetString(root, nameof(ModelReference.Provider), out var storedProvider)
            || !TryGetString(root, nameof(ModelReference.ModelId), out var storedModelId)
            || !string.Equals(storedProvider, provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(storedModelId, modelId, StringComparison.OrdinalIgnoreCase))
            return null;

        var recovered = new ModelReference { Provider = storedProvider, ModelId = storedModelId };
        if (TryGetInt32(root, nameof(ModelReference.ContextWindow), out var window))
            recovered.ContextWindow = window;

        return recovered;
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString()!;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt32(JsonElement root, string name, out int value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            // Web-default reads coerce numeric strings, so accept both a JSON number and a
            // stringified integer to match what the strict path would have parsed.
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out value))
                return true;
            if (property.Value.ValueKind == JsonValueKind.String
                && int.TryParse(property.Value.GetString(), out value))
                return true;
        }

        value = 0;
        return false;
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
/// An operator's intent for an overridable, operator-owned model attribute on <c>model set</c> —
/// a modality set (<see cref="ModelModality"/>) or the context window (<see cref="int"/>). A plain
/// <c>T?</c> cannot express it, because two of the three states both resolve to null yet behave
/// oppositely: "not supplied" must preserve any existing override, while "clear" must win over it.
/// The tri-state is: <see cref="Unset"/> (leave it to the stored value / discovery),
/// <see cref="Set"/> (replace with an explicit value), and <see cref="Clear"/> (remove the override
/// so runtime detection resolves it).
/// </summary>
internal readonly record struct ValueOverride<T>(bool Supplied, T? Value)
    where T : struct
{
    /// <summary>Operator said nothing — preserve the stored value, else fall back to discovery.</summary>
    internal static ValueOverride<T> Unset => default;

    /// <summary>Operator asked to remove the override so runtime capability detection resolves it.</summary>
    internal static ValueOverride<T> Clear => new(true, null);

    /// <summary>Operator supplied an explicit value that replaces whatever is stored.</summary>
    internal static ValueOverride<T> Set(T value) => new(true, value);
}

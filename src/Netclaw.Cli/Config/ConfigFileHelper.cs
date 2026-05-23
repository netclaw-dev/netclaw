// -----------------------------------------------------------------------
// <copyright file="ConfigFileHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Config;

/// <summary>
/// Shared helpers for reading and writing netclaw.json and secrets.json config files.
/// Extracted from McpCommand for reuse by ProviderCommand, ModelCommand, and TUI flows.
/// </summary>
internal static class ConfigFileHelper
{
    /// <summary>
    /// Load both netclaw.json and secrets.json as mutable dictionaries.
    /// Missing files get a default <c>{ "configVersion": 1 }</c> skeleton.
    /// </summary>
    internal static (Dictionary<string, object> config, Dictionary<string, object> secrets)
        LoadConfigFiles(Configuration.NetclawPaths paths)
    {
        var config = LoadJsonDict(paths.NetclawConfigPath);
        var secrets = LoadJsonDict(paths.SecretsPath);
        return (config, secrets);
    }

    /// <summary>
    /// Load a JSON file as a mutable dictionary. Returns a default skeleton if the file doesn't exist.
    /// </summary>
    internal static Dictionary<string, object> LoadJsonDict(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object> { ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion };

        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(text)
            ?? new Dictionary<string, object> { ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion };
    }

    /// <summary>
    /// Get or create a nested dictionary section. Handles JsonElement deserialization
    /// when the section was loaded from a file.
    /// </summary>
    internal static Dictionary<string, object> GetOrCreateSection(
        Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var existing) && existing is not null)
        {
            if (existing is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Null)
                {
                    var fresh = new Dictionary<string, object>();
                    dict[key] = fresh;
                    return fresh;
                }

                var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText())
                    ?? [];
                dict[key] = parsed;
                return parsed;
            }

            return (Dictionary<string, object>)existing;
        }

        var section = new Dictionary<string, object>();
        dict[key] = section;
        return section;
    }

    /// <summary>
    /// Get an existing nested dictionary section, or null if it doesn't exist.
    /// </summary>
    internal static Dictionary<string, object>? GetSectionOrNull(
        Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var existing))
            return null;

        if (existing is JsonElement je)
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText())
                ?? [];
            dict[key] = parsed;
            return parsed;
        }

        return existing as Dictionary<string, object>;
    }

    /// <summary>
    /// Serialize a config dictionary and write it to disk, creating parent directories if needed.
    /// </summary>
    internal static void WriteConfigFile(string path, Dictionary<string, object> data)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonDefaults.Indented));
    }

    /// <summary>
    /// Serialize and write secrets.json using hardened permissions and encryption-at-rest.
    /// </summary>
    internal static void WriteSecretsFile(Configuration.NetclawPaths paths, Dictionary<string, object> data)
    {
        var protector = SecretsProtection.CreateProtector(paths);
        SecretsFileWriter.Write(paths.SecretsPath, data, options: JsonDefaults.Indented, protector: protector);
    }

    internal static string DecryptIfEncrypted(Configuration.NetclawPaths paths, string? value)
    {
        if (string.IsNullOrEmpty(value) || !ISecretsProtector.IsEncrypted(value))
            return value ?? string.Empty;

        var protector = SecretsProtection.CreateProtector(paths);
        return protector.Unprotect(value);
    }

    /// <summary>
    /// Override fields that survive a model swap: when the operator changes a
    /// role's (Provider, ModelId), any of these fields present on the old role
    /// record are moved into <c>Models.Catalog["{provider}/{modelId}"]</c> so
    /// they re-apply if the operator switches back. Provider/ModelId/Provenance
    /// are NOT overrides — they identify which model is selected, not how it
    /// behaves.
    /// </summary>
    private static readonly string[] OverrideFieldNames =
        ["ContextWindow", "InputModalities", "OutputModalities"];

    /// <summary>
    /// Before overwriting <c>Models[roleKey]</c> with a new selection, move
    /// any override fields on the OLD role record into
    /// <c>Models.Catalog["{oldProvider}/{oldModelId}"]</c>. Inline values from
    /// the old role win over any existing catalog entry, because they
    /// represent the most recent operator intent. No-op when the old role
    /// record carries no override fields (the common case — most operators
    /// rely on auto-detection).
    /// </summary>
    internal static void PromoteRoleOverridesToCatalog(
        Dictionary<string, object> modelsSection, string roleKey)
    {
        var oldRole = GetSectionOrNull(modelsSection, roleKey);
        if (oldRole is null)
            return;

        var oldProvider = TryReadString(oldRole, "Provider");
        var oldModelId = TryReadString(oldRole, "ModelId");
        if (string.IsNullOrEmpty(oldProvider) || string.IsNullOrEmpty(oldModelId))
            return;

        Dictionary<string, object>? overrides = null;
        foreach (var name in OverrideFieldNames)
        {
            if (!oldRole.TryGetValue(name, out var value) || value is null)
                continue;
            overrides ??= new Dictionary<string, object>();
            overrides[name] = UnwrapJsonElement(value);
        }

        if (overrides is null)
            return;

        var catalog = GetOrCreateSection(modelsSection, "Catalog");
        var key = Configuration.ModelSelection.CatalogKey(oldProvider, oldModelId);
        var entry = GetOrCreateSection(catalog, key);
        foreach (var kvp in overrides)
            entry[kvp.Key] = kvp.Value;
    }

    /// <summary>
    /// Returns the (Provider, ModelId) currently recorded at
    /// <c>modelsSection[roleKey]</c>, or null when the role is absent or
    /// missing either identity field. Used by writers to detect identity
    /// changes vs same-identity re-runs.
    /// </summary>
    internal static (string Provider, string ModelId)? TryReadRoleIdentity(
        Dictionary<string, object> modelsSection, string roleKey)
    {
        var role = GetSectionOrNull(modelsSection, roleKey);
        if (role is null) return null;
        var provider = TryReadString(role, "Provider");
        var modelId = TryReadString(role, "ModelId");
        return string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(modelId)
            ? null
            : (provider, modelId);
    }

    /// <summary>
    /// Best-effort string read from a dictionary that may carry raw strings
    /// (re-materialized values) or <see cref="JsonElement"/>s (freshly loaded
    /// from disk). Returns null for missing, null, or non-string values.
    /// </summary>
    internal static string? TryReadString(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var raw) || raw is null)
            return null;
        return raw switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => null,
        };
    }

    private static object UnwrapJsonElement(object value) => value switch
    {
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.String => (object)(je.GetString() ?? string.Empty),
            JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => je.GetRawText(),
        },
        _ => value,
    };
}

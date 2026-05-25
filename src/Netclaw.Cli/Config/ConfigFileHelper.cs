// -----------------------------------------------------------------------
// <copyright file="ConfigFileHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
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
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonDefaults.ConfigFile));
    }

    /// <summary>
    /// Serialize and write secrets.json using hardened permissions and encryption-at-rest.
    /// </summary>
    internal static void WriteSecretsFile(Configuration.NetclawPaths paths, Dictionary<string, object> data)
    {
        var protector = SecretsProtection.CreateProtector(paths);
        SecretsFileWriter.Write(paths.SecretsPath, data, options: JsonDefaults.Indented, protector: protector);
    }

    internal static bool PathPresent(Dictionary<string, object> root, string path)
        => TryGetPathValue(root, path, out _);

    internal static bool TryGetPathValue(Dictionary<string, object> root, string path, out object? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        object? current = root;

        foreach (var segment in segments)
        {
            if (!TryGetChildValue(current, segment, out current))
            {
                value = null;
                return false;
            }
        }

        value = NormalizeNodeValue(current);
        return true;
    }

    internal static void SetPathValue(Dictionary<string, object> root, string path, object? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, object> current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            current = GetOrCreateSection(current, segment);
        }

        current[segments[^1]] = value!;
    }

    internal static bool RemovePath(Dictionary<string, object> root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, object>? current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = current is null ? null : GetSectionOrNull(current, segments[i]);
            if (current is null)
                return false;
        }

        if (current is null)
            return false;

        var removed = current.Remove(segments[^1]);
        if (!removed)
            return false;

        PruneEmptySections(root, segments);
        return true;
    }

    internal static bool SecretPresent(Configuration.NetclawPaths paths, string path)
    {
        var secrets = LoadJsonDict(paths.SecretsPath);
        return PathPresent(secrets, path);
    }

    internal static string DecryptIfEncrypted(Configuration.NetclawPaths paths, string? value)
    {
        if (string.IsNullOrEmpty(value) || !ISecretsProtector.IsEncrypted(value))
            return value ?? string.Empty;

        var protector = SecretsProtection.CreateProtector(paths);
        return protector.Unprotect(value);
    }

    private static bool TryGetChildValue(object? current, string segment, out object? child)
    {
        switch (current)
        {
            case Dictionary<string, object> dict when dict.TryGetValue(segment, out child):
                return true;
            case JsonObject jsonObject when jsonObject.TryGetPropertyValue(segment, out var node):
                child = node;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var property):
                child = property;
                return true;
            default:
                child = null;
                return false;
        }
    }

    private static object? NormalizeNodeValue(object? value)
        => value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.Object
                => JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText()),
            JsonElement element when element.ValueKind == JsonValueKind.Array
                => JsonSerializer.Deserialize<object[]>(element.GetRawText()),
            JsonElement element when element.ValueKind == JsonValueKind.String
                => element.GetString(),
            JsonElement element when element.ValueKind == JsonValueKind.True
                => true,
            JsonElement element when element.ValueKind == JsonValueKind.False
                => false,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var longValue)
                => longValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number
                => element.GetDouble(),
            JsonNode node => node.Deserialize<object>(),
            _ => value
        };

    private static void PruneEmptySections(Dictionary<string, object> root, string[] segments)
    {
        for (var depth = segments.Length - 1; depth > 0; depth--)
        {
            var parentPath = string.Join('.', segments.Take(depth));
            if (!TryGetPathValue(root, parentPath, out var parentValue)
                || parentValue is not Dictionary<string, object> parentSection)
            {
                continue;
            }

            if (parentSection.Count != 0)
                break;

            RemovePath(root, parentPath);
        }
    }
}

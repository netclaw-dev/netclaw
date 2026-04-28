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
            return new Dictionary<string, object> { ["configVersion"] = 1 };

        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(text)
            ?? new Dictionary<string, object> { ["configVersion"] = 1 };
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
                    ?? new Dictionary<string, object>();
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
                ?? new Dictionary<string, object>();
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
}

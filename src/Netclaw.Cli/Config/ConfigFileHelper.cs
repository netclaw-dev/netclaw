// -----------------------------------------------------------------------
// <copyright file="ConfigFileHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
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
    /// Appended to TUI status messages after a config write. True whether or not a daemon is
    /// running: the daemon's config watcher reloads on change, and with none running the next
    /// start reads the file.
    /// </summary>
    internal const string DaemonAppliesChange = "A running daemon applies the change automatically.";

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
    /// Binds the <c>Tools</c> section of netclaw.json the same way as the daemon: on top of the
    /// defaults for the configured posture. A missing file gives the defaults.
    /// </summary>
    /// <remarks>
    /// Do not deserialize <see cref="ToolConfig"/> from the raw JSON. A deserializer replaces a
    /// partial audience profile with an empty one, so an unset <c>McpServersMode</c> reads as an
    /// empty allowlist while the daemon reads the posture default. An editor that saves from that
    /// view narrows the profile (issue #2362).
    /// </remarks>
    internal static ToolConfig LoadToolConfig(Configuration.NetclawPaths paths)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .Build();
        return PolicyConfiguration.Bind(configuration).Tools;
    }

    /// <summary>
    /// Returns <paramref name="path"/> with each segment replaced by the key that the file already
    /// has, when one differs only in letter case. The daemon reads keys without case, so a write
    /// to <c>AllowedTools</c> beside an existing <c>allowedTools</c> gives a duplicate key, and
    /// the daemon then stops at startup.
    /// </summary>
    internal static string ResolveExistingKeyPath(Dictionary<string, object> root, string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        object? current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            // A loaded file holds nested objects as JsonElement until a writer converts them.
            IEnumerable<string>? keys = current switch
            {
                Dictionary<string, object> dictionary => dictionary.Keys,
                JsonElement { ValueKind: JsonValueKind.Object } element => element.EnumerateObject().Select(static property => property.Name),
                _ => null
            };
            var segment = segments[i];
            var existing = keys?.FirstOrDefault(key => string.Equals(key, segment, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                break;

            segments[i] = existing;
            current = current is Dictionary<string, object> parent ? parent[existing] : ((JsonElement)current!).GetProperty(existing);
        }

        return string.Join('.', segments);
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
    /// Load a JSON file as a mutable dictionary, or <c>null</c> when the file is missing or empty.
    /// Distinct from <see cref="LoadJsonDict"/>, which returns a <c>{ "configVersion": 1 }</c>
    /// skeleton for a missing file — callers that need to detect "no existing config" use this.
    /// </summary>
    internal static Dictionary<string, object>? LoadJsonDictOrNull(string path)
    {
        if (!File.Exists(path))
            return null;

        var config = LoadJsonDict(path);
        return config.Count == 0 ? null : config;
    }

    /// <summary>
    /// Like <see cref="LoadJsonDictOrNull"/> but never throws on an unreadable / malformed file:
    /// returns the parsed dict (or <c>null</c> when missing/empty/unreadable) and, via
    /// <paramref name="error"/>, a human-readable reason when the file existed but could not be read.
    /// Lets a view-model constructor degrade to a safe default and surface the error instead of
    /// crashing the page on open when netclaw.json is hand-corrupted or its keys directory is gone.
    /// </summary>
    internal static Dictionary<string, object>? TryLoadJsonDictOrNull(string path, out string? error)
    {
        error = null;
        try
        {
            return LoadJsonDictOrNull(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"Could not read {Path.GetFileName(path)}: {ex.Message}";
            return null;
        }
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
    /// Deserialize a loaded config section value into <typeparamref name="T"/>. The value may be a
    /// <see cref="JsonElement"/> (freshly loaded from disk) or an already-materialized CLR object
    /// (just written in-memory) — both shapes are handled. Returns <c>default</c> when the value
    /// deserializes to null; callers decide whether that means a fresh instance or an error.
    /// </summary>
    internal static T? DeserializeSection<T>(object raw)
    {
        var json = raw is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(raw, JsonDefaults.ConfigFile);
        return JsonSerializer.Deserialize<T>(json, JsonDefaults.ConfigRead);
    }

    /// <summary>
    /// Read a typed config section out of a loaded config dictionary, returning <c>new T()</c>
    /// when the section is absent, null, or deserializes to null.
    /// </summary>
    internal static T LoadSection<T>(Dictionary<string, object> root, string sectionName) where T : new()
    {
        if (!root.TryGetValue(sectionName, out var raw) || raw is null)
            return new T();

        return DeserializeSection<T>(raw) ?? new T();
    }

    /// <summary>
    /// Serialize a config dictionary and write it to disk, creating parent directories if needed.
    /// The file keeps its mode (an owner-only netclaw.json stays owner-only), and a symbolic link is
    /// followed so the file it points at is rewritten and the link stays a link. When the link's
    /// target is not writable (permission denied, read-only, or busy), the link itself is replaced; any other write error is thrown.
    /// </summary>
    internal static void WriteConfigFile(string path, Dictionary<string, object> data)
        => WriteConfigFile(path, data, AtomicFile.WriteAllText);

    // The writer is a parameter so a test can make the write fail like a full disk.
    internal static void WriteConfigFile(
        string path,
        Dictionary<string, object> data,
        Action<string, string, Action<string>?> atomicWrite)
    {
        var info = new FileInfo(path);
        var target = info.LinkTarget is null ? path : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        var json = JsonSerializer.Serialize(data, JsonDefaults.ConfigFile);

        try
        {
            WriteKeepingMode(target, data, json, atomicWrite);
        }
        catch (Exception ex) when (target != path && CanReplaceLink(ex))
        {
            // The link's target cannot take a new file: another owner, a read-only file system, or
            // a single file mounted into a container (busy). Replace the link itself, as saves did
            // before links were followed. Any other write failure, such as a full disk, is a save
            // error: replacing the link would detach it.
            WriteKeepingMode(path, data, json, atomicWrite);
        }
    }

    // .NET on Unix puts the errno in IOException.HResult. These values are the same on Linux and macOS.
    private const int BusyErrno = 16;
    private const int ReadOnlyFileSystemErrno = 30;

    /// <summary>
    /// True for the write errors that mean the link's target cannot take the file: permission
    /// denied, a read-only file system, or a busy file (a single-file bind mount).
    /// </summary>
    internal static bool CanReplaceLink(Exception ex)
        => ex is UnauthorizedAccessException
           || (ex is IOException { HResult: BusyErrno or ReadOnlyFileSystemErrno } && !OperatingSystem.IsWindows());

    private static void WriteKeepingMode(string path, Dictionary<string, object> data, string json, Action<string, string, Action<string>?> atomicWrite)
    {
        PreserveLegacyModelsBackup(path, data);
        atomicWrite(path, json, temp => AtomicFile.CopyUnixMode(path, temp));
    }

    private static void PreserveLegacyModelsBackup(string path, Dictionary<string, object> data)
    {
        if (!File.Exists(path)
            || GetSectionOrNull(data, "Models") is not { } newModels
            || !newModels.ContainsKey("Definitions"))
            return;

        using var existing = JsonDocument.Parse(File.ReadAllText(path));
        if (!existing.RootElement.TryGetProperty("Models", out var oldModels)
            || oldModels.ValueKind != JsonValueKind.Object
            || !oldModels.TryGetProperty("Main", out _))
            return;

        ModelEntryWriter.ThrowIfLegacyEnvironmentOverride();

        File.Copy(path, Doctor.DoctorFixService.NextBackupPath(path, "legacy-models"), overwrite: false);
    }

    /// <summary>
    /// Serialize and write secrets.json using hardened permissions and encryption-at-rest.
    /// </summary>
    internal static void WriteSecretsFile(Configuration.NetclawPaths paths, Dictionary<string, object> data)
    {
        var protector = SecretsProtection.CreateProtector(paths);
        SecretsFileWriter.Write(paths.SecretsPath, data, options: JsonDefaults.Indented, protector: protector);
    }

    internal static void UpdateSecretsFile(
        Configuration.NetclawPaths paths,
        Func<Dictionary<string, object>, bool, bool> update,
        ISecretsProtector? protector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        UpdateSecretsFile<object?>(
            paths,
            (secrets, fileExisted) => (update(secrets, fileExisted), null),
            protector,
            cancellationToken);
    }

    internal static TResult UpdateSecretsFile<TResult>(
        Configuration.NetclawPaths paths,
        Func<Dictionary<string, object>, bool, (bool Write, TResult Result)> update,
        ISecretsProtector? protector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var effectiveProtector = protector ?? SecretsProtection.CreateProtector(paths);
        return SecretsFileWriter.Update(
            paths.SecretsPath,
            (root, fileExisted) =>
            {
                var secrets = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                  root.ToJsonString(JsonDefaults.ConfigFile),
                                  JsonDefaults.ConfigRead)
                              ?? [];
                var outcome = update(secrets, fileExisted);
                if (!outcome.Write)
                    return (null, outcome.Result);

                var updatedRoot = JsonSerializer.SerializeToNode(secrets, JsonDefaults.ConfigFile)?.AsObject()
                                  ?? [];
                return (updatedRoot, outcome.Result);
            },
            effectiveProtector,
            JsonDefaults.Indented,
            cancellationToken);
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

    /// <summary>
    /// Read a secret value from secrets.json at <paramref name="path"/>, decrypting it if it was
    /// stored encrypted-at-rest. Returns <c>null</c> when the file or path is absent. Deliberately
    /// does NOT apply whitespace normalization — credential surfaces differ on whether a blank
    /// value means "null", "empty string", or a trimmed value, so each caller applies its own
    /// policy to the result.
    /// </summary>
    internal static string? ReadDecryptedSecret(Configuration.NetclawPaths paths, string path)
    {
        var secrets = LoadJsonDict(paths.SecretsPath);
        return TryGetPathValue(secrets, path, out var value)
            ? DecryptIfEncrypted(paths, value?.ToString())
            : null;
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

// -----------------------------------------------------------------------
// <copyright file="OAuthStateStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Netclaw.Configuration;

/// <summary>
/// Reads and writes oauth-state.json, which holds the token expiry of each OAuth provider.
/// </summary>
/// <remarks>
/// The expiry cannot go in secrets.json, because encrypted dates break configuration binding.
/// It cannot go in netclaw.json, because the config watcher restarts the daemon on each
/// write to that file. The file is a configuration source after secrets.json, so
/// <see cref="ValidateShape"/> rejects every key other than <c>Providers:&lt;name&gt;:OAuthTokenExpiry</c>.
/// A <c>null</c> value is kept on purpose: it hides a retired <c>OAuthTokenExpiry</c> key in netclaw.json.
/// A read accepts comments and trailing commas, as the JSON configuration provider does. A write drops them.
/// </remarks>
public static class OAuthStateStore
{
    private const string ProvidersKey = "Providers";
    private const string ExpiryKey = nameof(ProviderEntry.OAuthTokenExpiry);

    internal static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly object WriteGate = new();

    /// <summary>
    /// True when the file has an entry for the provider. <paramref name="expiry"/> is null when the
    /// entry says that the token has no expiry.
    /// </summary>
    public static bool TryGetExpiry(NetclawPaths paths, string providerName, out DateTimeOffset? expiry)
    {
        expiry = null;
        lock (WriteGate)
        {
            if (Load(paths) is not { } root
                || root[ProvidersKey] is not JsonObject providers
                || FindKey(providers, providerName) is not { } key
                || providers[key] is not JsonObject provider
                || FindKey(provider, ExpiryKey) is not { } expiryKey)
            {
                return false;
            }

            expiry = ParseExpiry(provider[expiryKey]);
            return true;
        }
    }

    /// <summary>True when the file has an entry for the provider, whether or not the entry has an expiry.</summary>
    public static bool Contains(NetclawPaths paths, string providerName)
    {
        lock (WriteGate)
        {
            return Load(paths) is { } root
                && root[ProvidersKey] is JsonObject providers
                && FindKey(providers, providerName) is not null;
        }
    }

    public static void SetExpiry(NetclawPaths paths, string providerName, DateTimeOffset? expiry)
    {
        lock (WriteGate)
        {
            var root = Load(paths) ?? [];
            var providers = root[ProvidersKey] as JsonObject ?? [];
            root[ProvidersKey] = providers;

            var key = FindKey(providers, providerName) ?? providerName;
            var provider = providers[key] as JsonObject ?? [];
            providers[key] = provider;

            provider[FindKey(provider, ExpiryKey) ?? ExpiryKey] = expiry?.ToString("o", CultureInfo.InvariantCulture);
            Save(paths, root);
        }
    }

    public static void Remove(NetclawPaths paths, string providerName)
    {
        lock (WriteGate)
        {
            if (Load(paths) is not { } root
                || root[ProvidersKey] is not JsonObject providers
                || FindKey(providers, providerName) is not { } key)
            {
                return;
            }

            providers.Remove(key);
            Save(paths, root);
        }
    }

    public static void Rename(NetclawPaths paths, string oldName, string newName)
    {
        lock (WriteGate)
        {
            if (Load(paths) is not { } root
                || root[ProvidersKey] is not JsonObject providers
                || FindKey(providers, oldName) is not { } key)
            {
                return;
            }

            var entry = providers[key];
            providers.Remove(key);
            providers[newName] = entry;
            Save(paths, root);
        }
    }

    /// <summary>One retired <c>OAuthTokenExpiry</c> key in a parsed netclaw.json.</summary>
    /// <param name="Provider">The provider name, as netclaw.json spells it.</param>
    /// <param name="Entry">The provider entry that holds the key.</param>
    /// <param name="Key">The key, as netclaw.json spells it.</param>
    /// <param name="Expiry">The date that the key holds, or null when it holds none.</param>
    public sealed record RetiredExpiryKey(string Provider, JsonObject Entry, string Key, DateTimeOffset? Expiry);

    /// <summary>
    /// Finds each <c>Providers.&lt;name&gt;.OAuthTokenExpiry</c> key in a parsed netclaw.json. Older
    /// releases wrote the key there.
    /// </summary>
    public static IReadOnlyList<RetiredExpiryKey> FindRetiredExpiryKeys(JsonObject netclawJson)
    {
        if (FindKey(netclawJson, ProvidersKey) is not { } providersKey || netclawJson[providersKey] is not JsonObject providers)
            return [];

        var keys = new List<RetiredExpiryKey>();
        foreach (var (name, node) in providers)
        {
            if (node is JsonObject entry && FindKey(entry, ExpiryKey) is { } key)
                keys.Add(new RetiredExpiryKey(name, entry, key, ParseExpiry(entry[key])));
        }

        return keys;
    }

    /// <summary>
    /// Copies each retired expiry in netclaw.json to oauth-state.json when the file has no entry for
    /// the provider, so that removing the key from netclaw.json loses no expiry. Returns the copy count.
    /// </summary>
    public static int MoveRetiredExpiries(NetclawPaths paths, JsonObject netclawJson)
    {
        var moved = 0;
        foreach (var retired in FindRetiredExpiryKeys(netclawJson))
        {
            if (retired.Expiry is not { } expiry || TryGetExpiry(paths, retired.Provider, out _))
                continue;

            SetExpiry(paths, retired.Provider, expiry);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// The providers whose expiry comes from the retired <c>OAuthTokenExpiry</c> key in netclaw.json,
    /// because oauth-state.json has no entry for them.
    /// </summary>
    public static IReadOnlyList<string> FindRetiredExpiryInUse(NetclawPaths paths)
    {
        if (!File.Exists(paths.NetclawConfigPath)
            || JsonNode.Parse(File.ReadAllText(paths.NetclawConfigPath), documentOptions: ReadOptions) is not JsonObject config)
        {
            return [];
        }

        return FindRetiredExpiryKeys(config)
            .Where(static retired => retired.Entry[retired.Key] is not null)
            .Select(static retired => retired.Provider)
            .Where(name => !TryGetExpiry(paths, name, out _))
            .ToList();
    }

    private static JsonObject? Load(NetclawPaths paths)
    {
        if (!File.Exists(paths.OAuthStatePath))
            return null;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(paths.OAuthStatePath), documentOptions: ReadOptions);
            ValidateShape(root);
            return (JsonObject)root!;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"Couldn't read oauth-state.json: {ex.Message}", ex);
        }
    }

    private static void Save(NetclawPaths paths, JsonObject root)
        => AtomicFile.WriteAllText(paths.OAuthStatePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    // The configuration binder reads an empty string as null for a nullable date, so an empty value is allowed.
    internal static void ValidateShape(JsonNode? root)
    {
        if (root is not JsonObject top)
            throw new InvalidDataException("the top level is not a JSON object");

        foreach (var (topKey, providersNode) in top)
        {
            if (!string.Equals(topKey, ProvidersKey, StringComparison.OrdinalIgnoreCase))
                throw NotAllowed(topKey);

            if (providersNode is null)
                continue;

            if (providersNode is not JsonObject providers)
                throw new InvalidDataException($"'{topKey}' is not an object.");

            foreach (var (name, entryNode) in providers)
            {
                var entryPath = $"{topKey}:{name}";
                if (entryNode is null)
                    continue;

                if (entryNode is not JsonObject entry)
                    throw new InvalidDataException($"'{entryPath}' is not an object.");

                foreach (var (key, value) in entry)
                {
                    var path = $"{entryPath}:{key}";
                    if (!string.Equals(key, ExpiryKey, StringComparison.OrdinalIgnoreCase))
                        throw NotAllowed(path);

                    if (!IsDateOrEmpty(value))
                        throw new InvalidDataException($"'{path}' is not a date: {value?.ToJsonString()}.");
                }
            }
        }
    }

    private static InvalidDataException NotAllowed(string path)
        => new($"key '{path}' is not allowed. The file holds only {ProvidersKey}:<name>:{ExpiryKey}.");

    private static bool IsDateOrEmpty(JsonNode? node)
        => node is null
            || node is JsonValue value && value.TryGetValue<string>(out var text) && (text.Length == 0 || TryParseDate(text, out _));

    private static DateTimeOffset? ParseExpiry(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && TryParseDate(text, out var parsed)
            ? parsed
            : null;

    private static bool TryParseDate(string text, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);

    // The configuration binder reads keys without case, so a lookup does the same.
    private static string? FindKey(JsonObject parent, string name)
        => parent.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed class OAuthStateConfigurationSource : JsonConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new OAuthStateConfigurationProvider(this);
    }
}

internal sealed class OAuthStateConfigurationProvider(OAuthStateConfigurationSource source) : JsonConfigurationProvider(source)
{
    public override void Load(Stream stream)
    {
        // Each parse closes the stream it reads, so each one gets its own.
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        base.Load(new MemoryStream(bytes));

        OAuthStateStore.ValidateShape(JsonNode.Parse(new MemoryStream(bytes), documentOptions: OAuthStateStore.ReadOptions));
    }
}

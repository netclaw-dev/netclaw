// -----------------------------------------------------------------------
// <copyright file="OAuthTokenExpiryStorage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Configuration;

/// <summary>
/// Owner of the value rules for <c>Providers.&lt;name&gt;.OAuthTokenExpiry</c>. Netclaw keeps this
/// value in secrets.json. <see cref="SecretsFileWriter.IsPlaintextLeaf"/> keeps it unencrypted so the
/// configuration binder can read it as a date. Older releases wrote the key to netclaw.json. That key is
/// retired: it still binds until secrets.json holds a value, and <c>netclaw doctor --fix</c> moves it.
/// </summary>
public static class OAuthTokenExpiryStorage
{
    private const string ProvidersKey = "Providers";
    private const string ExpiryKey = nameof(ProviderEntry.OAuthTokenExpiry);

    // The daemon reads netclaw.json with comments and trailing commas allowed.
    private static readonly JsonDocumentOptions ConfigReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Parse the stored text with the rule of the configuration binder. Null or empty text is a
    /// valid "no expiry" value, because the binder reads an empty string as null.
    /// </summary>
    public static bool TryParse(string? text, out DateTimeOffset? expiry)
    {
        if (string.IsNullOrEmpty(text))
        {
            expiry = null;
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            expiry = parsed;
            return true;
        }

        expiry = null;
        return false;
    }

    /// <summary>
    /// Parse the stored text and fail loudly when it is not a date. <paramref name="location"/>
    /// names the file and key for the operator.
    /// </summary>
    public static DateTimeOffset? Parse(string? text, string location)
    {
        if (TryParse(text, out var expiry))
            return expiry;

        throw new InvalidDataException($"{location} is not a date: '{text}'.");
    }

    /// <summary>One retired <c>OAuthTokenExpiry</c> key in a parsed netclaw.json.</summary>
    /// <param name="Provider">The provider name, as netclaw.json spells it.</param>
    /// <param name="Entry">The provider entry that holds the key.</param>
    /// <param name="Key">The key, as netclaw.json spells it.</param>
    /// <param name="Value">The value of the key.</param>
    public sealed record RetiredKey(string Provider, JsonObject Entry, string Key, JsonNode? Value);

    /// <summary>
    /// Finds each <c>Providers.&lt;name&gt;.OAuthTokenExpiry</c> key in a parsed netclaw.json. The
    /// lookup ignores case, as the configuration binder does.
    /// </summary>
    public static IReadOnlyList<RetiredKey> FindRetired(JsonObject netclawJson)
    {
        ArgumentNullException.ThrowIfNull(netclawJson);
        if (FindKey(netclawJson, ProvidersKey) is not { } providersKey || netclawJson[providersKey] is not JsonObject providers)
            return [];

        var keys = new List<RetiredKey>();
        foreach (var (name, node) in providers)
        {
            if (node is JsonObject entry && FindKey(entry, ExpiryKey) is { } key)
                keys.Add(new RetiredKey(name, entry, key, entry[key]));
        }

        return keys;
    }

    /// <summary>
    /// Copies each retired date in <paramref name="netclawJson"/> into the matching secrets.json
    /// provider entry, in one update. Returns the copy count. A provider is skipped when secrets.json has
    /// no entry for it (it has no tokens, so no expiry to keep) or when the entry already holds the key,
    /// a JSON null included (the secrets.json value wins). No value moves unless it is a date.
    /// </summary>
    public static int MoveRetiredToSecrets(NetclawPaths paths, JsonObject netclawJson, ISecretsProtector protector)
    {
        var retired = FindRetired(netclawJson);
        if (retired.Count == 0 || !File.Exists(paths.SecretsPath))
            return 0;

        return SecretsFileWriter.Update<int>(paths.SecretsPath, (root, _) =>
        {
            var moved = 0;
            if (FindKey(root, ProvidersKey) is not { } providersKey || root[providersKey] is not JsonObject providers)
                return (null, 0);

            foreach (var key in retired)
            {
                if (!TryGetDate(key.Value, out var date)
                    || date is not { } parsed
                    || FindKey(providers, key.Provider) is not { } providerKey
                    || providers[providerKey] is not JsonObject node
                    || FindKey(node, ExpiryKey) is not null)
                {
                    continue;
                }

                // Canonical form, as OAuthTokenPersistence writes it.
                node[ExpiryKey] = parsed.ToString("o", CultureInfo.InvariantCulture);
                moved++;
            }

            return (moved > 0 ? root : null, moved);
        }, protector);
    }

    /// <summary>
    /// One startup warning for each retired key that holds a value. Reads netclaw.json with comments and
    /// trailing commas allowed. A parse failure throws: the configuration provider read the same file a
    /// moment before.
    /// </summary>
    public static IReadOnlyList<string> RetiredKeyWarnings(NetclawPaths paths)
    {
        if (!File.Exists(paths.NetclawConfigPath)
            || JsonNode.Parse(File.ReadAllText(paths.NetclawConfigPath), documentOptions: ConfigReadOptions) is not JsonObject config)
        {
            return [];
        }

        return FindRetired(config)
            .Where(static key => key.Value is not null)
            .Select(static key =>
                $"Providers:{key.Provider}:OAuthTokenExpiry in netclaw.json is retired. "
                + "Netclaw keeps the token expiry in secrets.json. Run `netclaw doctor --fix` to move it.")
            .ToList();
    }

    /// <summary>
    /// True for a value that the binder reads without error: a JSON null, or a string that is empty or
    /// parses as a date. <c>netclaw doctor --fix</c> removes only such a retired key.
    /// </summary>
    public static bool IsNullOrDate(JsonNode? node)
        => node is null
           || node is JsonValue value && value.TryGetValue<string>(out var text) && TryParse(text, out _);

    // A string that parses as a date. JSON null, an empty string, and any other kind are not dates.
    private static bool TryGetDate(JsonNode? node, out DateTimeOffset? date)
    {
        date = null;
        return node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrEmpty(text)
            && TryParse(text, out date);
    }

    // The configuration binder reads keys without case, so a lookup does the same.
    private static string? FindKey(JsonObject parent, string name)
        => parent.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}

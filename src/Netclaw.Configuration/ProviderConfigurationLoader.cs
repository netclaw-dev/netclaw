// -----------------------------------------------------------------------
// <copyright file="ProviderConfigurationLoader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>
/// Loads provider entries from configuration while preserving provider-owned
/// vendor options as an opaque JSON bag.
/// </summary>
public static class ProviderConfigurationLoader
{
    public static Dictionary<string, ProviderEntry> Load(IConfigurationSection section)
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var providerSection in section.GetChildren())
        {
            var entry = BindProviderEntry(providerSection);
            entry.VendorOptions = BindVendorOptions(providerSection.GetSection(nameof(ProviderEntry.VendorOptions)));
            providers[providerSection.Key] = entry;
        }

        return providers;
    }

    private static ProviderEntry BindProviderEntry(IConfigurationSection section)
    {
        var entry = new ProviderEntry
        {
            Type = section[nameof(ProviderEntry.Type)] ?? "ollama",
            Endpoint = section[nameof(ProviderEntry.Endpoint)] ?? string.Empty,
            AuthMethod = ParseEnum(section[nameof(ProviderEntry.AuthMethod)], AuthMethod.None)
        };

        var apiKey = section[nameof(ProviderEntry.ApiKey)];
        if (!string.IsNullOrWhiteSpace(apiKey))
            entry.ApiKey = new SensitiveString(apiKey);

        var oauthAccessToken = section[nameof(ProviderEntry.OAuthAccessToken)];
        if (!string.IsNullOrWhiteSpace(oauthAccessToken))
            entry.OAuthAccessToken = new SensitiveString(oauthAccessToken);

        var oauthRefreshToken = section[nameof(ProviderEntry.OAuthRefreshToken)];
        if (!string.IsNullOrWhiteSpace(oauthRefreshToken))
            entry.OAuthRefreshToken = new SensitiveString(oauthRefreshToken);

        var oauthTokenExpiry = section[nameof(ProviderEntry.OAuthTokenExpiry)];
        if (!string.IsNullOrWhiteSpace(oauthTokenExpiry))
            entry.OAuthTokenExpiry = DateTimeOffset.Parse(oauthTokenExpiry, CultureInfo.InvariantCulture);

        return entry;
    }

    private static JsonObject? BindVendorOptions(IConfigurationSection section)
    {
        if (!section.Exists())
            return null;

        return BuildNode(section) as JsonObject
            ?? throw new InvalidOperationException("Providers:<name>:VendorOptions must be an object.");
    }

    private static JsonNode? BuildNode(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
            return CreateScalarNode(section.Value);

        if (children.All(static child => int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            var array = new JsonArray();
            foreach (var child in children.OrderBy(static child => int.Parse(child.Key, CultureInfo.InvariantCulture)))
                array.Add(BuildNode(child));
            return array;
        }

        var obj = new JsonObject();
        foreach (var child in children)
            obj[child.Key] = BuildNode(child);

        return obj;
    }

    private static JsonNode? CreateScalarNode(string? value)
    {
        if (value is null)
            return null;

        if (bool.TryParse(value, out var boolValue))
            return JsonValue.Create(boolValue);

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
            return JsonValue.Create(longValue);

        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
            return JsonValue.Create(decimalValue);

        return JsonValue.Create(value);
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return Enum.Parse<TEnum>(value, ignoreCase: true);
    }
}

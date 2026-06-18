// -----------------------------------------------------------------------
// <copyright file="SearchConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for the web search backend. Bound from the "Search" section
/// of netclaw.json (backend, endpoint) and secrets.json (API key).
/// </summary>
public sealed class SearchConfig
{
    /// <summary>
    /// When false, the web search subsystem is disabled.
    /// Search tools are not registered regardless of audience profile.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Search backend identifier.
    /// </summary>
    [ConfigValue(Key = "Search.Backend", PersistTo = ConfigPersistStore.NetclawJson)]
    public SearchBackend Backend { get; set; } = SearchBackend.DuckDuckGo;

    /// <summary>
    /// Brave Search API subscription token. Required when Backend is "brave".
    /// Stored in secrets.json under Search.BraveApiKey.
    /// </summary>
    [ConfigValue(Key = "Search.BraveApiKey", PersistTo = ConfigPersistStore.SecretsJson)]
    public SensitiveString? BraveApiKey { get; set; }

    /// <summary>
    /// SearXNG instance base URL (e.g., "http://searxng.local:8080").
    /// Required when Backend is "searxng".
    /// </summary>
    [ConfigValue(Key = "Search.SearXngEndpoint", PersistTo = ConfigPersistStore.NetclawJson)]
    public string? SearXngEndpoint { get; set; }
}

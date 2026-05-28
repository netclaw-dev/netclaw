// -----------------------------------------------------------------------
// <copyright file="SearchSectionSpec.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Config;

/// <summary>
/// Authoritative editor contract for the Search workflow. This is intentionally limited to
/// editor semantics and persisted-file behavior; runtime config loading continues to bind from
/// IConfiguration using the existing netclaw.json + secrets.json + environment overlay.
/// </summary>
internal sealed class SearchSectionSpec
{
    private static readonly string BackendPath = ConfigValueMetadataProvider.Get<SearchConfig>(nameof(SearchConfig.Backend)).Key;
    private static readonly string BraveApiKeyPath = ConfigValueMetadataProvider.Get<SearchConfig>(nameof(SearchConfig.BraveApiKey)).Key;
    private static readonly string SearXngEndpointPath = ConfigValueMetadataProvider.Get<SearchConfig>(nameof(SearchConfig.SearXngEndpoint)).Key;

    internal IReadOnlyList<ProjectedConfigField> Fields { get; } =
    [
        new(
            Path: BackendPath,
            PropertyName: nameof(SearchConfig.Backend),
            Label: "Backend",
            Description: "Search backend identifier.",
            ValueKind: ConfigFieldValueKind.String,
            Storage: ConfigFieldStorage.ConfigFile,
            Widget: ConfigFieldWidget.EnumSelection,
            Nullable: false,
            DefaultValue: SearchBackend.DuckDuckGo.ToWireValue(),
            TrimDefaultOnSave: true,
            PreserveBlankSecret: false,
            Placeholder: null,
            Hint: "Choose your web search provider.",
            ApplicableWhenPath: null,
            ApplicableWhenEquals: null,
            InactiveText: null,
            EnumOptions:
            [
                new("duckduckgo", "DuckDuckGo"),
                new("brave", "Brave"),
                new("searxng", "SearXng (self-hosted)")
            ]),
        new(
            Path: BraveApiKeyPath,
            PropertyName: nameof(SearchConfig.BraveApiKey),
            Label: "Brave API key",
            Description: "Brave Search API key. Required when Backend is Brave. Stored in secrets.json.",
            ValueKind: ConfigFieldValueKind.String,
            Storage: ConfigFieldStorage.SecretsFile,
            Widget: ConfigFieldWidget.PasswordInput,
            Nullable: true,
            DefaultValue: null,
            TrimDefaultOnSave: false,
            PreserveBlankSecret: true,
            Placeholder: "Enter Brave Search API key...",
            Hint: "Stored in secrets.json. Leave blank to keep the existing key.",
            ApplicableWhenPath: BackendPath,
            ApplicableWhenEquals: "brave",
            InactiveText: "(not configured)",
            EnumOptions: []),
        new(
            Path: SearXngEndpointPath,
            PropertyName: nameof(SearchConfig.SearXngEndpoint),
            Label: "SearXng instance URL",
            Description: "SearXNG instance base URL. Required when Backend is SearXng.",
            ValueKind: ConfigFieldValueKind.String,
            Storage: ConfigFieldStorage.ConfigFile,
            Widget: ConfigFieldWidget.TextInput,
            Nullable: true,
            DefaultValue: null,
            TrimDefaultOnSave: true,
            PreserveBlankSecret: false,
            Placeholder: "https://search.example.com",
            Hint: "Enter the base URL of your SearXNG instance.",
            ApplicableWhenPath: BackendPath,
            ApplicableWhenEquals: "searxng",
            InactiveText: "(not configured)",
            EnumOptions: [])
    ];

    internal ProjectedConfigField? GetProviderField(SearchEditorModel model)
        => model.Backend switch
        {
            SearchBackend.Brave => GetField(BraveApiKeyPath),
            SearchBackend.SearXng => GetField(SearXngEndpointPath),
            _ => null,
        };

    internal string GetProviderDescription(string backend)
        => backend switch
        {
            "brave" => "Brave Search requires an API key and is usually more reliable than DuckDuckGo.",
            "searxng" => "SearXNG uses your own endpoint URL and supports self-hosted search.",
            _ => "DuckDuckGo works without setup, but may hit bot detection.",
        };

    internal string GetEntryTitle(ProjectedConfigField field)
        => field.Path switch
        {
            var path when path == BraveApiKeyPath
                => "Brave Search requires an API key.",
            _ => "Enter the base URL of your SearXNG instance.",
        };

    internal string GetEntryHint(ProjectedConfigField field, SearchEditorModel model)
        => field.Path switch
        {
            var path when path == BraveApiKeyPath
                && model.Brave.HasPersistedApiKey
                => "Stored in secrets.json. Leave blank to keep the existing key. Press Enter to validate and save.",
            var path when path == BraveApiKeyPath
                => "Stored in secrets.json. Press Enter to validate and save.",
            _ => "Netclaw will validate the URL and probe it on Enter.",
        };

    internal string GetValidatingMessage(SearchEditorModel model)
        => model.Backend switch
        {
            SearchBackend.Brave => "Probing Brave Search",
            SearchBackend.SearXng => "Probing SearXNG instance",
            _ => "Validating DuckDuckGo configuration",
        };

    internal string GetSavedMessage(SearchEditorModel model)
        => $"✔ {GetBackendLabel(model.Backend)} validated and saved.";

    internal string GetSavedNextStepText()
        => "Press Esc to return to Search backends or Up/Down to review providers.";

    internal string GetBackendLabel(SearchBackend backend)
        => backend switch
        {
            SearchBackend.Brave => "Brave",
            SearchBackend.SearXng => "SearXng (self-hosted)",
            _ => "DuckDuckGo",
        };

    private ProjectedConfigField GetField(string path)
        => Fields.First(field => string.Equals(field.Path, path, StringComparison.Ordinal));
}

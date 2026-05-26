// -----------------------------------------------------------------------
// <copyright file="SearchConfigEditorViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http;
using Netclaw.Configuration;
using Netclaw.Search;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

internal enum SearchConfigEditorDialog
{
    None,
    ProbeWarning,
}

internal enum SearchConfigEditorScreen
{
    Summary,
}

internal sealed record SearchProbeResult(bool Success, string Message, ConfigStatusTone Tone);

internal sealed record SearchFieldCommitResult(bool Success, IReadOnlyList<SearchEditorValidationIssue> Issues)
{
    public static readonly SearchFieldCommitResult Ok = new(true, []);

    public static SearchFieldCommitResult Invalid(IReadOnlyList<SearchEditorValidationIssue> issues)
        => new(false, issues);
}

internal sealed class SearchConfigEditorViewModel : ReactiveViewModel
{
    private readonly NetclawPaths _paths;
    private readonly SearchEditorPersistenceMapper _mapper;
    private readonly SearchEditorValidationAdapter _validator;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private SearchEditorModel _model;
    private SearchEditorValidationResult _validation = SearchEditorValidationResult.Empty;
    private SearchProbeResult? _lastProbeResult;

    public IReadOnlyList<ProjectedConfigField> Fields { get; } =
    [
        new(
            Path: "Search.Backend",
            PropertyName: "Backend",
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
        SearchFields.BraveApiKey,
        SearchFields.SearXngEndpoint,
    ];

    public Dictionary<string, ReactiveProperty<string>> FieldValues { get; } = new(StringComparer.Ordinal);

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public SearchConfigEditorViewModel(
        NetclawPaths paths,
        IHttpClientFactory? httpClientFactory = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _mapper = new SearchEditorPersistenceMapper();
        _validator = new SearchEditorValidationAdapter();
        _model = _mapper.Load(paths);

        foreach (var field in Fields)
            FieldValues[field.Path] = new ReactiveProperty<string>(GetCurrentFieldValue(field.Path));

        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        ValidationSummary = new ReactiveProperty<ConfigValidationSummary>(ConfigValidationSummary.Empty);
        ActiveDialog = new ReactiveProperty<SearchConfigEditorDialog>(SearchConfigEditorDialog.None);
        CurrentScreen = new ReactiveProperty<SearchConfigEditorScreen>(SearchConfigEditorScreen.Summary);
        Revalidate();
    }

    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<ConfigValidationSummary> ValidationSummary { get; }
    public ReactiveProperty<SearchConfigEditorDialog> ActiveDialog { get; }
    public ReactiveProperty<SearchConfigEditorScreen> CurrentScreen { get; }

    public bool IsDirty => ComputeIsDirty();
    public SearchProbeResult? LastProbeResult => _lastProbeResult;
    public string CurrentBackendValue => _model.Backend.ToWireValue();
    public string CurrentBackendLabel => _model.Backend switch
    {
        SearchBackend.Brave => "Brave",
        SearchBackend.SearXng => "SearXng (self-hosted)",
        _ => "DuckDuckGo",
    };

    public IReadOnlyList<ConfigEnumOption> BackendOptions { get; } =
    [
        new("duckduckgo", "DuckDuckGo"),
        new("brave", "Brave"),
        new("searxng", "SearXng (self-hosted)")
    ];

    public ProjectedConfigField? CurrentProviderField => _model.Backend switch
    {
        SearchBackend.Brave => SearchFields.BraveApiKey,
        SearchBackend.SearXng => SearchFields.SearXngEndpoint,
        _ => null,
    };

    public override void Dispose()
    {
        foreach (var value in FieldValues.Values)
            value.Dispose();

        Status.Dispose();
        ValidationSummary.Dispose();
        ActiveDialog.Dispose();
        CurrentScreen.Dispose();
        base.Dispose();
    }

    public SearchFieldCommitResult CommitField(string path, string? value)
    {
        ApplyFieldValue(path, value);
        SyncFieldValue(path);
        ClearTransientProbeState();
        Revalidate();

        var issues = _validation.IssuesFor(path);
        if (issues.Count > 0)
        {
            Status.Value = new ConfigStatusMessage(issues[0].Message, ConfigStatusTone.Error);
            RequestRedraw();
            return SearchFieldCommitResult.Invalid(issues);
        }

        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        RequestRedraw();
        return SearchFieldCommitResult.Ok;
    }

    public string GetDisplayValue(ProjectedConfigField field)
        => field.Path switch
        {
            "Search.BraveApiKey" when !string.IsNullOrWhiteSpace(_model.Brave.ApiKeyDraft) => "(new secret entered)",
            "Search.BraveApiKey" when _model.Brave.HasPersistedApiKey => "(stored secret preserved)",
            "Search.SearXngEndpoint" when !string.IsNullOrWhiteSpace(_model.SearXng.Endpoint) => _model.SearXng.Endpoint!,
            _ => field.InactiveText ?? string.Empty,
        };

    public string GetEditorSeed(ProjectedConfigField field)
        => GetCurrentFieldValue(field.Path);

    public IReadOnlyList<ConfigValidationIssue> GetIssues(ProjectedConfigField field)
        => ValidationSummary.Value.IssuesFor(field.Path);

    public IReadOnlyList<ConfigValidationIssue> GetCurrentProviderIssues()
        => CurrentProviderField is { } field ? ValidationSummary.Value.IssuesFor(field.Path) : [];

    public string GetSummaryStateText()
        => _model.Backend switch
        {
            SearchBackend.Brave => HasEffectiveBraveKey() ? "API key configured." : "API key required.",
            SearchBackend.SearXng => !string.IsNullOrWhiteSpace(_model.SearXng.Endpoint) ? "Endpoint configured." : "Endpoint required.",
            _ => "No additional setup required."
        };

    public ConfigStatusTone GetSummaryStateTone()
        => _model.Backend switch
        {
            SearchBackend.Brave when !HasEffectiveBraveKey() => ConfigStatusTone.Warning,
            SearchBackend.SearXng when string.IsNullOrWhiteSpace(_model.SearXng.Endpoint) => ConfigStatusTone.Warning,
            _ => ConfigStatusTone.Neutral,
        };

    public string? GetCurrentProviderSupportText()
        => _model.Backend switch
        {
            SearchBackend.Brave when _model.Brave.HasPersistedApiKey => "Existing key is configured. Leave blank to keep it.",
            SearchBackend.SearXng => "Enter the base URL of your SearXNG instance.",
            _ => null,
        };

    public bool HasPersistedSecret(string path)
        => string.Equals(path, "Search.BraveApiKey", StringComparison.Ordinal) && _model.Brave.HasPersistedApiKey;

    public void SetFieldValue(string path, string? value)
        => CommitField(path, value);

    public void StageFieldValue(string path, string? value)
    {
        if (!FieldValues.TryGetValue(path, out var property))
            throw new InvalidOperationException($"Unknown search config field '{path}'.");

        property.Value = value ?? string.Empty;
    }

    public void CommitFieldValue(string path)
    {
        if (!FieldValues.TryGetValue(path, out var property))
            throw new InvalidOperationException($"Unknown search config field '{path}'.");

        CommitField(path, property.Value);
    }

    public void CommitCurrentProviderDraft()
    {
        if (CurrentProviderField is { } field)
            CommitFieldValue(field.Path);
    }

    public void BeginBackendSelection()
    {
        CurrentScreen.Value = SearchConfigEditorScreen.Summary;
        RequestRedraw();
    }

    public void SelectBackendForEditing(string backend)
    {
        CommitField("Search.Backend", backend);
        CurrentScreen.Value = SearchConfigEditorScreen.Summary;
        RequestRedraw();
    }

    public void ReturnToSummary()
    {
        CurrentScreen.Value = SearchConfigEditorScreen.Summary;
        RequestRedraw();
    }

    public void DismissDialog()
    {
        ActiveDialog.Value = SearchConfigEditorDialog.None;
        RequestRedraw();
    }

    public async Task TestCurrentConfigurationAsync(CancellationToken ct = default)
    {
        Revalidate();
        if (_validation.HasErrors)
        {
            Status.Value = new ConfigStatusMessage(
                "Fix structural validation errors before testing this search configuration.",
                ConfigStatusTone.Error);
            RequestRedraw();
            return;
        }

        _lastProbeResult = await ProbeAsync(ct);
        Status.Value = new ConfigStatusMessage(_lastProbeResult.Message, _lastProbeResult.Tone);
        RequestRedraw();
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        Revalidate();
        if (_validation.HasErrors)
        {
            Status.Value = new ConfigStatusMessage(
                "Fix structural validation errors before saving.",
                ConfigStatusTone.Error);
            RequestRedraw();
            return;
        }

        _lastProbeResult = await ProbeAsync(ct);
        if (!_lastProbeResult.Success)
        {
            Status.Value = new ConfigStatusMessage(_lastProbeResult.Message, ConfigStatusTone.Warning);
            ActiveDialog.Value = SearchConfigEditorDialog.ProbeWarning;
            RequestRedraw();
            return;
        }

        SaveWithoutProbeOverride();
    }

    public void SaveWithoutProbeOverride()
    {
        _mapper.Save(_paths, _model);
        _model = _mapper.Load(_paths);
        SyncAllFieldValues();
        Revalidate();
        ActiveDialog.Value = SearchConfigEditorDialog.None;
        CurrentScreen.Value = SearchConfigEditorScreen.Summary;
        Status.Value = new ConfigStatusMessage("Saved Search settings.", ConfigStatusTone.Success);
        RequestRedraw();
    }

    public void ResetDraft()
    {
        _model = _mapper.Load(_paths);
        SyncAllFieldValues();
        _lastProbeResult = null;
        Revalidate();
        Status.Value = new ConfigStatusMessage("Reverted unsaved Search edits.", ConfigStatusTone.Neutral);
        RequestRedraw();
    }

    public void NavigateBack()
    {
        RouteRequested?.Invoke("/config");
        Navigate?.Invoke("/config");
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    private void Revalidate()
    {
        _validation = _validator.Validate(_model);
        ValidationSummary.Value = new ConfigValidationSummary(
            _validation.Issues.Select(static issue => new ConfigValidationIssue(issue.FieldId, issue.Severity, issue.Message)).ToList());
    }

    private void ClearTransientProbeState()
    {
        _lastProbeResult = null;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
    }

    private void ApplyFieldValue(string path, string? value)
    {
        switch (path)
        {
            case "Search.Backend":
                _model.Backend = ParseBackend(value);
                break;
            case "Search.BraveApiKey":
                _model.Brave.ApiKeyDraft = Normalize(value);
                break;
            case "Search.SearXngEndpoint":
                _model.SearXng.Endpoint = Normalize(value);
                break;
            default:
                throw new InvalidOperationException($"Unknown search config field '{path}'.");
        }
    }

    private string GetCurrentFieldValue(string path)
        => path switch
        {
            "Search.Backend" => _model.Backend.ToWireValue(),
            "Search.BraveApiKey" => _model.Brave.ApiKeyDraft ?? string.Empty,
            "Search.SearXngEndpoint" => _model.SearXng.Endpoint ?? string.Empty,
            _ => string.Empty,
        };

    private void SyncFieldValue(string path)
    {
        if (FieldValues.TryGetValue(path, out var property))
            property.Value = GetCurrentFieldValue(path);
    }

    private void SyncAllFieldValues()
    {
        foreach (var field in Fields)
            SyncFieldValue(field.Path);
    }

    private async Task<SearchProbeResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            ISearchBackend searchBackend = _model.Backend switch
            {
                SearchBackend.Brave => new BraveSearchBackend(
                    _model.Brave.ApiKeyDraft ?? string.Empty,
                    CreateHttpClient(),
                    _timeProvider),
                SearchBackend.SearXng => new SearXngBackend(
                    _model.SearXng.Endpoint ?? string.Empty,
                    CreateHttpClient(),
                    _timeProvider),
                _ => new DuckDuckGoBackend(CreateHttpClient(), _timeProvider),
            };

            var result = await searchBackend.SearchAsync("netclaw", 1, ct);
            return result switch
            {
                SearchBackendResult.Success => new SearchProbeResult(true, "Search backend test succeeded.", ConfigStatusTone.Success),
                SearchBackendResult.Error error => new SearchProbeResult(false, error.Message, ConfigStatusTone.Warning),
                _ => new SearchProbeResult(false, "Search backend test failed.", ConfigStatusTone.Warning),
            };
        }
        catch (Exception ex)
        {
            return new SearchProbeResult(false, $"Search backend test failed: {ex.Message}", ConfigStatusTone.Warning);
        }
    }

    private HttpClient CreateHttpClient()
        => _httpClientFactory?.CreateClient(string.Empty) ?? new HttpClient();

    private bool HasEffectiveBraveKey()
        => !string.IsNullOrWhiteSpace(_model.Brave.ApiKeyDraft) || _model.Brave.HasPersistedApiKey;

    private bool ComputeIsDirty()
    {
        var persisted = _mapper.Load(_paths);
        return persisted.Backend != _model.Backend
            || !string.Equals(persisted.SearXng.Endpoint, _model.SearXng.Endpoint, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(_model.Brave.ApiKeyDraft);
    }

    private static SearchBackend ParseBackend(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "brave" => SearchBackend.Brave,
            "searxng" => SearchBackend.SearXng,
            _ => SearchBackend.DuckDuckGo,
        };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static class SearchFields
    {
        internal static readonly ProjectedConfigField BraveApiKey = new(
            Path: "Search.BraveApiKey",
            PropertyName: "BraveApiKey",
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
            ApplicableWhenPath: "Search.Backend",
            ApplicableWhenEquals: "brave",
            InactiveText: "(not configured)",
            EnumOptions: []);

        internal static readonly ProjectedConfigField SearXngEndpoint = new(
            Path: "Search.SearXngEndpoint",
            PropertyName: "SearXngEndpoint",
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
            ApplicableWhenPath: "Search.Backend",
            ApplicableWhenEquals: "searxng",
            InactiveText: "(not configured)",
            EnumOptions: []);
    }
}

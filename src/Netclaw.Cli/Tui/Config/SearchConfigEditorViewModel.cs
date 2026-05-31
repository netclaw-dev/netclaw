// -----------------------------------------------------------------------
// <copyright file="SearchConfigEditorViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http;
using System.Threading;
using Netclaw.Cli.Config;
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
    ProviderSelection,
    Entry,
    Validating,
    Saved,
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
    private readonly SearchSectionSpec _spec;
    private readonly NetclawPaths _paths;
    private readonly SearchEditorPersistenceMapper _mapper;
    private readonly SearchEditorValidationAdapter _validator;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private SearchEditorModel _model;
    private SearchEditorValidationResult _validation = SearchEditorValidationResult.Empty;
    private SearchProbeResult? _lastProbeResult;
    private CancellationTokenSource? _validationSpinnerCts;

    public IReadOnlyList<ProjectedConfigField> Fields => _spec.Fields;

    public Dictionary<string, ReactiveProperty<string>> FieldValues { get; } = new(StringComparer.Ordinal);

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public SearchConfigEditorViewModel(
        NetclawPaths paths,
        IHttpClientFactory? httpClientFactory = null,
        TimeProvider? timeProvider = null)
    {
        _spec = new SearchSectionSpec();
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
        CurrentScreen = new ReactiveProperty<SearchConfigEditorScreen>(SearchConfigEditorScreen.ProviderSelection);
        ValidationSpinnerTick = new ReactiveProperty<int>(0);
        Revalidate();
    }

    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<ConfigValidationSummary> ValidationSummary { get; }
    public ReactiveProperty<SearchConfigEditorDialog> ActiveDialog { get; }
    public ReactiveProperty<SearchConfigEditorScreen> CurrentScreen { get; }
    public ReactiveProperty<int> ValidationSpinnerTick { get; }

    public bool IsDirty => ComputeIsDirty();
    public SearchProbeResult? LastProbeResult => _lastProbeResult;
    public string CurrentBackendValue => _model.Backend.ToWireValue();
    public string CurrentBackendLabel => _spec.GetBackendLabel(_model.Backend);

    public IReadOnlyList<ConfigEnumOption> BackendOptions { get; } =
    [
        new("duckduckgo", "DuckDuckGo"),
        new("brave", "Brave"),
        new("searxng", "SearXng (self-hosted)")
    ];

    public ProjectedConfigField? CurrentProviderField => _spec.GetProviderField(_model);

    public bool IsCurrentBackendConfigured => _model.Backend switch
    {
        SearchBackend.Brave => HasEffectiveBraveKey(),
        SearchBackend.SearXng => !string.IsNullOrWhiteSpace(_model.SearXng.Endpoint),
        _ => true,
    };

    public string GetProviderDescription(string backend) => _spec.GetProviderDescription(backend);

    public string GetEntryTitle(ProjectedConfigField field) => _spec.GetEntryTitle(field);

    public string GetEntryHint(ProjectedConfigField field) => _spec.GetEntryHint(field, _model);

    public string GetValidatingMessage() => _spec.GetValidatingMessage(_model);

    public string GetSavedMessage() => _spec.GetSavedMessage(_model);

    public string GetSavedNextStepText() => _spec.GetSavedNextStepText();

    public override void Dispose()
    {
        CancelValidationSpinner();
        foreach (var value in FieldValues.Values)
            value.Dispose();

        Status.Dispose();
        ValidationSummary.Dispose();
        ActiveDialog.Dispose();
        CurrentScreen.Dispose();
        ValidationSpinnerTick.Dispose();
        base.Dispose();
    }

    public SearchFieldCommitResult CommitField(string path, string? value)
    {
        StageFieldValue(path, value);
        var candidate = CloneModel(_model);
        ApplyFieldValue(candidate, path, value);

        var candidateValidation = _validator.Validate(candidate);
        var issues = candidateValidation.IssuesFor(path);

        if (issues.Count > 0)
        {
            Status.Value = new ConfigStatusMessage(issues[0].Message, ConfigStatusTone.Error);
            RequestRedraw();
            return SearchFieldCommitResult.Invalid(issues);
        }

        _model = candidate;
        SyncFieldValue(path);
        ClearTransientProbeState();
        Revalidate();
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
        => FieldValues.TryGetValue(field.Path, out var property)
            ? property.Value
            : GetCurrentFieldValue(field.Path);

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
        CancelValidationSpinner();
        ActiveDialog.Value = SearchConfigEditorDialog.None;
        CurrentScreen.Value = SearchConfigEditorScreen.ProviderSelection;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        RequestRedraw();
    }

    public void SelectBackendForEditing(string backend)
    {
        CommitField("Search.Backend", backend);
        ActiveDialog.Value = SearchConfigEditorDialog.None;
        CurrentScreen.Value = SearchConfigEditorScreen.Entry;
        RequestRedraw();
    }

    public void ReturnToSummary()
    {
        BeginBackendSelection();
        RequestRedraw();
    }

    public void DismissDialog()
    {
        ActiveDialog.Value = SearchConfigEditorDialog.None;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        RequestRedraw();
    }

    public async Task<bool> SubmitCurrentConfigurationAsync(CancellationToken ct = default)
    {
        if (CurrentProviderField is { } field)
        {
            var result = CommitField(field.Path, FieldValues[field.Path].Value);
            if (!result.Success)
            {
                CurrentScreen.Value = SearchConfigEditorScreen.Entry;
                RequestRedraw();
                return false;
            }
        }
        else
        {
            ClearTransientProbeState();
            Revalidate();
            if (_validation.HasErrors)
            {
                Status.Value = BuildValidationErrorStatus("Fix structural validation errors before continuing.");
                CurrentScreen.Value = SearchConfigEditorScreen.Entry;
                RequestRedraw();
                return false;
            }
        }

        return await RunDynamicValidationAsync(persistOnSuccess: true, ct);
    }

    public async Task TestCurrentConfigurationAsync(CancellationToken ct = default)
    {
        Revalidate();
        if (_validation.HasErrors)
        {
            Status.Value = BuildValidationErrorStatus(
                "Fix structural validation errors before testing this search configuration.");
            CurrentScreen.Value = SearchConfigEditorScreen.Entry;
            RequestRedraw();
            return;
        }

        await RunDynamicValidationAsync(persistOnSuccess: false, ct);
    }

    public async Task SaveAsync(CancellationToken ct = default)
        => await SubmitCurrentConfigurationAsync(ct);

    internal async Task SubmitCurrentConfigurationFromInputAsync(CancellationToken ct = default)
    {
        try
        {
            await SubmitCurrentConfigurationAsync(ct);
        }
        catch (Exception ex)
        {
            CancelValidationSpinner();
            CurrentScreen.Value = SearchConfigEditorScreen.Entry;
            Status.Value = new ConfigStatusMessage($"Search settings save failed: {ex.Message}", ConfigStatusTone.Error);
            RequestRedraw();
        }
    }

    public void SaveWithoutProbeOverride()
    {
        CancelValidationSpinner();
        Revalidate();
        if (_validation.HasErrors)
        {
            ActiveDialog.Value = SearchConfigEditorDialog.None;
            CurrentScreen.Value = SearchConfigEditorScreen.Entry;
            Status.Value = BuildValidationErrorStatus("Fix structural validation errors before saving this search configuration.");
            RequestRedraw();
            return;
        }

        _mapper.Save(_paths, _model);
        ReloadPersistedDraft();
        ActiveDialog.Value = SearchConfigEditorDialog.None;
        CurrentScreen.Value = SearchConfigEditorScreen.Saved;
        Status.Value = new ConfigStatusMessage("Saved Search settings.", ConfigStatusTone.Success);
        RequestRedraw();
    }

    public void ResetDraft()
    {
        ReloadPersistedDraft();
        Status.Value = new ConfigStatusMessage("Reverted unsaved Search edits.", ConfigStatusTone.Neutral);
        RequestRedraw();
    }

    public void NavigateBack()
    {
        CancelValidationSpinner();
        ReloadPersistedDraft();
        ActiveDialog.Value = SearchConfigEditorDialog.None;
        CurrentScreen.Value = SearchConfigEditorScreen.ProviderSelection;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        RouteRequested?.Invoke("/config");
        Navigate?.Invoke("/config");
    }

    public void RequestQuit()
    {
        CancelValidationSpinner();
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

    private static SearchEditorModel CloneModel(SearchEditorModel source)
    {
        var clone = new SearchEditorModel
        {
            Backend = source.Backend,
        };

        clone.Brave.ApiKeyDraft = source.Brave.ApiKeyDraft;
        clone.Brave.HasPersistedApiKey = source.Brave.HasPersistedApiKey;
        clone.SearXng.Endpoint = source.SearXng.Endpoint;
        return clone;
    }

    private static void ApplyFieldValue(SearchEditorModel model, string path, string? value)
    {
        switch (path)
        {
            case "Search.Backend":
                model.Backend = ParseBackend(value);
                break;
            case "Search.BraveApiKey":
                model.Brave.ApiKeyDraft = Normalize(value);
                break;
            case "Search.SearXngEndpoint":
                model.SearXng.Endpoint = Normalize(value);
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

    private void ReloadPersistedDraft()
    {
        CancelValidationSpinner();
        _model = _mapper.Load(_paths);
        SyncAllFieldValues();
        _lastProbeResult = null;
        Revalidate();
        ActiveDialog.Value = SearchConfigEditorDialog.None;
        CurrentScreen.Value = SearchConfigEditorScreen.ProviderSelection;
    }

    private async Task<bool> RunDynamicValidationAsync(bool persistOnSuccess, CancellationToken ct)
    {
        ActiveDialog.Value = SearchConfigEditorDialog.None;
        CurrentScreen.Value = SearchConfigEditorScreen.Validating;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        StartValidationSpinner(ct);
        RequestRedraw();

        _lastProbeResult = await ProbeAsync(ct);
        if (!_lastProbeResult.Success)
        {
            CancelValidationSpinner();
            CurrentScreen.Value = SearchConfigEditorScreen.Entry;
            Status.Value = new ConfigStatusMessage(_lastProbeResult.Message, ConfigStatusTone.Warning);
            ActiveDialog.Value = SearchConfigEditorDialog.ProbeWarning;
            RequestRedraw();
            return false;
        }

        CancelValidationSpinner();
        if (persistOnSuccess)
        {
            SaveWithoutProbeOverride();
            return true;
        }

        CurrentScreen.Value = SearchConfigEditorScreen.Entry;
        Status.Value = new ConfigStatusMessage(_lastProbeResult.Message, _lastProbeResult.Tone);
        RequestRedraw();
        return true;
    }

    private void StartValidationSpinner(CancellationToken ct)
    {
        CancelValidationSpinner();
        ValidationSpinnerTick.Value = 0;
        _validationSpinnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = RunValidationSpinnerAsync(_validationSpinnerCts.Token);
    }

    private void CancelValidationSpinner()
    {
        _validationSpinnerCts?.Cancel();
        _validationSpinnerCts?.Dispose();
        _validationSpinnerCts = null;
        ValidationSpinnerTick.Value = 0;
    }

    private async Task RunValidationSpinnerAsync(CancellationToken ct)
    {
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(120, ct); }
            catch (OperationCanceledException) { return; }

            tick++;
            ValidationSpinnerTick.Value = tick;
            RequestRedraw();
        }
    }

    private async Task<SearchProbeResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            ISearchBackend searchBackend = _model.Backend switch
            {
                SearchBackend.Brave => new BraveSearchBackend(
                    GetEffectiveBraveApiKey(),
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

    private string GetEffectiveBraveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_model.Brave.ApiKeyDraft))
            return _model.Brave.ApiKeyDraft;

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        return ConfigFileHelper.TryGetPathValue(secrets, "Search.BraveApiKey", out var braveRaw)
            ? ConfigFileHelper.DecryptIfEncrypted(_paths, braveRaw?.ToString()) ?? string.Empty
            : string.Empty;
    }

    private bool HasEffectiveBraveKey()
        => !string.IsNullOrWhiteSpace(_model.Brave.ApiKeyDraft) || _model.Brave.HasPersistedApiKey;

    private bool ComputeIsDirty()
    {
        var persisted = _mapper.Load(_paths);
        return persisted.Backend != _model.Backend
            || !string.Equals(persisted.SearXng.Endpoint, _model.SearXng.Endpoint, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(_model.Brave.ApiKeyDraft);
    }

    private ConfigStatusMessage BuildValidationErrorStatus(string fallbackMessage)
    {
        var issue = GetCurrentProviderIssues().FirstOrDefault()
            ?? ValidationSummary.Value.Issues.FirstOrDefault();
        return issue is null
            ? new ConfigStatusMessage(fallbackMessage, ConfigStatusTone.Error)
            : new ConfigStatusMessage(issue.Message, ConfigStatusTone.Error);
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

}

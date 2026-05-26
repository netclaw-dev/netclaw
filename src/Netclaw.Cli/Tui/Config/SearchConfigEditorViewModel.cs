// -----------------------------------------------------------------------
// <copyright file="SearchConfigEditorViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
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
    Summary,
    ChooseProvider,
    ConfigureProvider,
}

internal sealed record SearchProbeResult(bool Success, string Message, ConfigStatusTone Tone);

internal sealed class SearchConfigEditorViewModel : ReactiveViewModel
{
    private readonly NetclawPaths _paths;
    private readonly ConfigSectionEditSession _session;
    private readonly JsonSchema _schema;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ProjectedConfigField> _fieldsByPath;
    private ConfigValidationSummary _lastStructuralValidation = ConfigValidationSummary.Empty;
    private SearchProbeResult? _lastProbeResult;

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

        var projector = new ConfigSectionSchemaProjector();
        Fields = projector.ProjectTopLevelSection("Search", SearchConfigMetadata.Fields);
        _fieldsByPath = Fields.ToDictionary(static field => field.Path, StringComparer.Ordinal);
        _session = new ConfigSectionEditSession(paths, Fields);

        var schemaText = EmbeddedSchemaLoader.LoadConfigSchema(EmbeddedSchemaLoader.CurrentSchemaVersion)
            ?? throw new InvalidOperationException(
                $"Missing embedded netclaw config schema v{EmbeddedSchemaLoader.CurrentSchemaVersion}.");
        _schema = JsonSchema.FromText(schemaText);

        SelectedIndex = new ReactiveProperty<int>(0);
        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        ValidationSummary = new ReactiveProperty<ConfigValidationSummary>(ConfigValidationSummary.Empty);
        ActiveDialog = new ReactiveProperty<SearchConfigEditorDialog>(SearchConfigEditorDialog.None);
        CurrentScreen = new ReactiveProperty<SearchConfigEditorScreen>(SearchConfigEditorScreen.Summary);

        foreach (var field in Fields)
            FieldValues[field.Path] = new ReactiveProperty<string>(_session.GetEditableString(field.Path));

        Revalidate();
    }

    public IReadOnlyList<ProjectedConfigField> Fields { get; }
    public Dictionary<string, ReactiveProperty<string>> FieldValues { get; } = new(StringComparer.Ordinal);
    public ReactiveProperty<int> SelectedIndex { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<ConfigValidationSummary> ValidationSummary { get; }
    public ReactiveProperty<SearchConfigEditorDialog> ActiveDialog { get; }
    public ReactiveProperty<SearchConfigEditorScreen> CurrentScreen { get; }

    public ProjectedConfigField SelectedField => Fields[SelectedIndex.Value];
    public bool IsDirty => _session.IsDirty;
    public SearchProbeResult? LastProbeResult => _lastProbeResult;
    public string CurrentBackendValue => _session.GetEffectiveString("Search.Backend") ?? SearchBackend.DuckDuckGo.ToWireValue();
    public string CurrentBackendLabel => GetField("Search.Backend").EnumOptions
        .FirstOrDefault(option => string.Equals(option.Value, CurrentBackendValue, StringComparison.OrdinalIgnoreCase))?.Label
        ?? CurrentBackendValue;
    public IReadOnlyList<ConfigEnumOption> BackendOptions => GetField("Search.Backend").EnumOptions;
    public ProjectedConfigField? CurrentProviderField => CurrentBackendValue switch
    {
        "brave" => GetField("Search.BraveApiKey"),
        "searxng" => GetField("Search.SearXngEndpoint"),
        _ => null,
    };

    public override void Dispose()
    {
        foreach (var value in FieldValues.Values)
            value.Dispose();

        SelectedIndex.Dispose();
        Status.Dispose();
        ValidationSummary.Dispose();
        ActiveDialog.Dispose();
        CurrentScreen.Dispose();
        base.Dispose();
    }

    public void MoveSelection(int delta)
    {
        if (Fields.Count == 0)
            return;

        var next = Math.Clamp(SelectedIndex.Value + delta, 0, Fields.Count - 1);
        if (next != SelectedIndex.Value)
            SelectedIndex.Value = next;
    }

    public void SetFieldValue(string path, string? value)
    {
        StageFieldValue(path, value);
        CommitFieldValue(path);
    }

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

        _session.SetValue(path, property.Value);
        ClearTransientProbeState();
        Revalidate();
        RequestRedraw();
    }

    public void CommitCurrentProviderDraft()
    {
        if (CurrentProviderField is { } field)
            CommitFieldValue(field.Path);
    }

    public string GetDisplayValue(ProjectedConfigField field)
    {
        if (field.Widget == ConfigFieldWidget.PasswordInput)
        {
            var edited = _session.GetEditableString(field.Path);
            if (!string.IsNullOrWhiteSpace(edited))
                return "(new secret entered)";
            if (_session.HasPersistedSecret(field.Path))
                return "(stored secret preserved)";
            return field.InactiveText ?? string.Empty;
        }

        var current = _session.GetEditableString(field.Path);
        if (!string.IsNullOrWhiteSpace(current))
            return current;

        return field.DefaultValue?.ToString() ?? string.Empty;
    }

    public string GetEditorSeed(ProjectedConfigField field)
        => FieldValues[field.Path].Value;

    public bool IsApplicable(ProjectedConfigField field) => _session.IsApplicable(field);

    public string GetInactiveText(ProjectedConfigField field)
        => field.InactiveText ?? "(not applicable)";

    public IReadOnlyList<ConfigValidationIssue> GetIssues(ProjectedConfigField field)
        => ValidationSummary.Value.IssuesFor(field.Path);

    public IReadOnlyList<ConfigValidationIssue> GetCurrentProviderIssues()
        => CurrentProviderField is { } field ? ValidationSummary.Value.IssuesFor(field.Path) : [];

    public string GetSummaryStateText()
        => CurrentBackendValue switch
        {
            "brave" => HasEffectiveValue("Search.BraveApiKey") ? "API key configured." : "API key required.",
            "searxng" => HasEffectiveValue("Search.SearXngEndpoint") ? "Endpoint configured." : "Endpoint required.",
            _ => "No additional setup required."
        };

    public ConfigStatusTone GetSummaryStateTone()
        => CurrentBackendValue switch
        {
            "brave" when !HasEffectiveValue("Search.BraveApiKey") => ConfigStatusTone.Warning,
            "searxng" when !HasEffectiveValue("Search.SearXngEndpoint") => ConfigStatusTone.Warning,
            _ => ConfigStatusTone.Neutral,
        };

    public string? GetCurrentProviderSupportText()
        => CurrentBackendValue switch
        {
            "brave" when HasPersistedSecret("Search.BraveApiKey") => "Existing key is configured. Leave blank to keep it.",
            "searxng" => "Enter the base URL of your SearXNG instance.",
            _ => null,
        };

    public bool HasPersistedSecret(string path) => _session.HasPersistedSecret(path);

    public void BeginBackendSelection()
    {
        CurrentScreen.Value = SearchConfigEditorScreen.Summary;
        RequestRedraw();
    }

    public void SelectBackendForEditing(string backend)
    {
        SetFieldValue("Search.Backend", backend);
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
        CommitCurrentProviderDraft();
        Revalidate();
        if (_lastStructuralValidation.HasErrors)
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
        CommitCurrentProviderDraft();
        Revalidate();
        if (_lastStructuralValidation.HasErrors)
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
        _session.Save();
        Revalidate();
        ActiveDialog.Value = SearchConfigEditorDialog.None;
        CurrentScreen.Value = SearchConfigEditorScreen.Summary;
        Status.Value = new ConfigStatusMessage("Saved Search settings.", ConfigStatusTone.Success);
        RequestRedraw();
    }

    public void ResetDraft()
    {
        _session.ResetDraft();
        foreach (var field in Fields)
            FieldValues[field.Path].Value = _session.GetEditableString(field.Path);

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
        _lastStructuralValidation = ValidateDraft();
        ValidationSummary.Value = _lastStructuralValidation;
    }

    private void ClearTransientProbeState()
    {
        _lastProbeResult = null;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
    }

    private ConfigValidationSummary ValidateDraft()
    {
        var draft = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        foreach (var field in Fields)
        {
            var value = _session.GetValue(field.Path);
            var shouldRemove = value is null
                || field.ValueKind == ConfigFieldValueKind.String && string.IsNullOrWhiteSpace(value.ToString())
                || field.TrimDefaultOnSave && Equals(value?.ToString(), field.DefaultValue?.ToString());

            if (shouldRemove)
                ConfigFileHelper.RemovePath(draft, field.Path);
            else
                ConfigFileHelper.SetPathValue(draft, field.Path, value);
        }

        draft["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        var node = JsonSerializer.SerializeToNode(draft) as JsonObject
            ?? throw new InvalidOperationException("Search config draft did not serialize to a JSON object.");

        var evaluation = _schema.Evaluate(node, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        var issues = new List<ConfigValidationIssue>();

        foreach (var field in Fields)
        {
            if (!IsApplicable(field))
                continue;

            if (field.Path == "Search.Backend" && string.IsNullOrWhiteSpace(_session.GetEffectiveString(field.Path)))
            {
                issues.Add(new ConfigValidationIssue(field.Path, ConfigValidationSeverity.Error, "Choose a search backend."));
            }

            if (field.Path == "Search.BraveApiKey"
                && string.Equals(_session.GetEffectiveString("Search.Backend"), "brave", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(_session.GetEffectiveString(field.Path)))
            {
                issues.Add(new ConfigValidationIssue(field.Path, ConfigValidationSeverity.Error, "Brave requires an API key."));
            }

            if (field.Path == "Search.SearXngEndpoint"
                && string.Equals(_session.GetEffectiveString("Search.Backend"), "searxng", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(_session.GetEffectiveString(field.Path)))
            {
                issues.Add(new ConfigValidationIssue(field.Path, ConfigValidationSeverity.Error, "SearXNG requires an endpoint URL."));
            }
        }

        if (!evaluation.IsValid && evaluation.Details is not null)
        {
            foreach (var detail in evaluation.Details.Where(static d => !d.IsValid && d.Errors is not null))
            {
                var path = MapSchemaInstanceLocationToField(detail.InstanceLocation?.ToString());
                if (path is null)
                    continue;

                var message = string.Join("; ", detail.Errors!.Select(e => $"{e.Key}: {e.Value}"));
                if (!issues.Any(i => i.Path == path && string.Equals(i.Message, message, StringComparison.Ordinal)))
                    issues.Add(new ConfigValidationIssue(path, ConfigValidationSeverity.Error, message));
            }
        }

        return issues.Count == 0 ? ConfigValidationSummary.Empty : new ConfigValidationSummary(issues);
    }

    private async Task<SearchProbeResult> ProbeAsync(CancellationToken ct)
    {
        var backend = _session.GetEffectiveString("Search.Backend") ?? SearchBackend.DuckDuckGo.ToWireValue();
        try
        {
            ISearchBackend searchBackend = backend switch
            {
                "brave" => new BraveSearchBackend(
                    _session.GetEffectiveString("Search.BraveApiKey") ?? string.Empty,
                    CreateHttpClient(),
                    _timeProvider),
                "searxng" => new SearXngBackend(
                    _session.GetEffectiveString("Search.SearXngEndpoint") ?? string.Empty,
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
        => _httpClientFactory?.CreateClient() ?? new HttpClient();

    private bool HasEffectiveValue(string path)
        => !string.IsNullOrWhiteSpace(_session.GetEffectiveString(path));

    private ProjectedConfigField GetField(string path)
        => _fieldsByPath.TryGetValue(path, out var field)
            ? field
            : throw new InvalidOperationException($"Unknown search config field '{path}'.");

    private static string? MapSchemaInstanceLocationToField(string? instanceLocation)
    {
        if (string.IsNullOrWhiteSpace(instanceLocation))
            return null;

        var path = instanceLocation.TrimStart('/').Replace('/', '.');
        return path.StartsWith("Search.", StringComparison.Ordinal) ? path : null;
    }
}

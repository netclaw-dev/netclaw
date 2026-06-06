// -----------------------------------------------------------------------
// <copyright file="SkillSourcesConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Netclaw.Actors.Skills;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

internal sealed record SkillFeedReachabilityResult(bool Success, string Message);

internal interface ISkillFeedReachabilityProbe
{
    SkillFeedReachabilityResult Probe(string baseUrl, string? apiKey, int timeoutSeconds);
}

internal sealed class SkillFeedReachabilityProbe : ISkillFeedReachabilityProbe
{
    public SkillFeedReachabilityResult Probe(string baseUrl, string? apiKey, int timeoutSeconds)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 10));
            using var cts = new CancellationTokenSource(timeout);
            using var client = new HttpClient { Timeout = timeout };
            var root = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(new Uri(root), ".well-known/agent-skills/index.json"));

            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = client.Send(request, cts.Token);
            if (response.IsSuccessStatusCode)
                return new SkillFeedReachabilityResult(true, "Skill feed discovery endpoint is reachable.");

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new SkillFeedReachabilityResult(false, $"Skill feed authentication failed with HTTP {(int)response.StatusCode}.");

            return new SkillFeedReachabilityResult(false, $"Skill feed probe returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return new SkillFeedReachabilityResult(false, $"Skill feed probe failed: {ex.Message}");
        }
    }
}

internal enum SkillSourceKind
{
    LocalFolder,
    RemoteSkillServer,
}

internal enum SkillSourcesScreen
{
    Inventory,
    SourceDetail,
    AddLocalPath,
    AddLocalSymlinks,
    AddLocalName,
    AddRemoteUrl,
    AddRemoteAuth,
    AddRemoteToken,
    AddRemoteName,
    RenameSource,
    ChangeLocation,
    RemoveConfirm,
}

internal enum SkillSourcesInventoryAction
{
    OpenSource,
    AddLocalFolder,
    AddSkillServer,
    RescanAll,
    Done,
}

internal enum SkillSourceDetailAction
{
    ToggleEnabled,
    Location,
    ToggleSymlinks,
    Rescan,
    Rename,
    ChangeLocation,
    Authentication,
    SyncInterval,
    TestConnection,
    RotateToken,
    RemoveToken,
    RemoveSource,
    Done,
}

internal enum SkillSourceAuthMode
{
    None,
    BearerToken,
}

internal sealed record SkillSourceDisplay(
    SkillSourceKind Kind,
    string Name,
    string Location,
    bool Enabled,
    bool IsWellKnown,
    bool AllowSymlinks,
    bool HasApiKey,
    int TimeoutSeconds,
    string StatusText,
    ConfigStatusTone StatusTone);

internal sealed record SkillSourcesInventoryRow(
    SkillSourcesInventoryAction Action,
    SkillSourceKind? SourceKind,
    string? SourceName,
    string Label,
    string Detail,
    ConfigStatusTone Tone);

internal sealed record SkillSourceDetailRow(
    SkillSourceDetailAction Action,
    string Label,
    string Detail,
    ConfigStatusTone Tone);

internal sealed record LocalSkillScanDisplay(int Count, string? Warning);

internal sealed class SkillSourcesConfigViewModel : ReactiveViewModel
{
    private const int DefaultFeedTimeoutSeconds = 30;

    private readonly NetclawPaths _paths;
    private readonly ISkillFeedReachabilityProbe _probe;
    private readonly StringComparer _nameComparer = StringComparer.OrdinalIgnoreCase;
    private string? _saveAnywayFingerprint;
    private List<SkillSourceDisplay> _sources = [];
    private SkillSourceKind? _selectedKind;
    private string? _selectedName;
    private string? _pendingLocalPath;
    private bool _pendingLocalAllowSymlinks;
    private string? _pendingRemoteUrl;
    private SkillSourceAuthMode _pendingRemoteAuthMode;
    private string? _pendingRemoteApiKey;
    private string? _pendingRemoteProbeMessage;
    private int _pendingRemoteTimeoutSeconds = DefaultFeedTimeoutSeconds;
    private SkillSourceDetailAction? _editingAction;

    public SkillSourcesConfigViewModel(NetclawPaths paths, ISkillFeedReachabilityProbe? probe = null)
    {
        _paths = paths;
        _probe = probe ?? new SkillFeedReachabilityProbe();
        Screen = new ReactiveProperty<SkillSourcesScreen>(SkillSourcesScreen.Inventory);
        SelectedRow = new ReactiveProperty<int>(0);
        Draft = new ReactiveProperty<string>(string.Empty);
        Version = new ReactiveProperty<int>(0);
        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        IsSaved = new ReactiveProperty<bool>(false);
        ReloadSources();
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<SkillSourcesScreen> Screen { get; }
    public ReactiveProperty<int> SelectedRow { get; }
    public ReactiveProperty<string> Draft { get; }
    public ReactiveProperty<int> Version { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<bool> IsSaved { get; }

    public IReadOnlyList<SkillSourceDisplay> Sources => _sources;

    public SkillSourceDisplay? SelectedSource => _selectedKind is { } kind && _selectedName is { Length: > 0 } name
        ? _sources.FirstOrDefault(s => s.Kind == kind && _nameComparer.Equals(s.Name, name))
        : null;

    public IReadOnlyList<SkillSourcesInventoryRow> InventoryRows => BuildInventoryRows();

    public IReadOnlyList<SkillSourceDetailRow> DetailRows => SelectedSource is { } source
        ? BuildDetailRows(source)
        : [];

    public bool IsTextEntryActive => IsTextEntryScreen(Screen.Value);

    public string CurrentTitle => Screen.Value switch
    {
        SkillSourcesScreen.Inventory => "Skill Sources",
        SkillSourcesScreen.SourceDetail when SelectedSource is { } source => $"Skill Sources > {source.Name}",
        SkillSourcesScreen.AddLocalPath => "Add Local Skill Folder",
        SkillSourcesScreen.AddLocalSymlinks => "Local Folder Security",
        SkillSourcesScreen.AddLocalName => "Review Local Folder",
        SkillSourcesScreen.AddRemoteUrl => "Add Skill Server",
        SkillSourcesScreen.AddRemoteAuth => "Skill Server Authentication",
        SkillSourcesScreen.AddRemoteToken => "Skill Server Token",
        SkillSourcesScreen.AddRemoteName => "Review Skill Server",
        SkillSourcesScreen.RenameSource => "Rename Skill Source",
        SkillSourcesScreen.ChangeLocation when SelectedSource?.Kind == SkillSourceKind.RemoteSkillServer => "Change Skill Server URL",
        SkillSourcesScreen.ChangeLocation => "Change Local Folder Path",
        SkillSourcesScreen.RemoveConfirm => "Remove Skill Source?",
        _ => "Skill Sources",
    };

    public void MoveSelection(int delta)
    {
        var count = RowCountForCurrentScreen();
        if (count == 0)
            return;

        var next = Math.Clamp(SelectedRow.Value + delta, 0, count - 1);
        if (next != SelectedRow.Value)
            SelectedRow.Value = next;
    }

    public void AppendText(string text)
    {
        if (!IsTextEntryScreen(Screen.Value))
            return;

        Draft.Value += text;
        MarkDirty();
    }

    public void Backspace()
    {
        if (!IsTextEntryScreen(Screen.Value) || Draft.Value.Length == 0)
            return;

        Draft.Value = Draft.Value[..^1];
        MarkDirty();
    }

    public void ActivateSelected()
    {
        switch (Screen.Value)
        {
            case SkillSourcesScreen.Inventory:
                ActivateInventoryRow();
                break;
            case SkillSourcesScreen.SourceDetail:
                ActivateDetailRow();
                break;
            case SkillSourcesScreen.AddLocalPath:
                ContinueAddLocalPath();
                break;
            case SkillSourcesScreen.AddLocalSymlinks:
                ContinueAddLocalSymlinks();
                break;
            case SkillSourcesScreen.AddLocalName:
                SaveNewLocalSource();
                break;
            case SkillSourcesScreen.AddRemoteUrl:
                ContinueAddRemoteUrl();
                break;
            case SkillSourcesScreen.AddRemoteAuth:
                ContinueAddRemoteAuth();
                break;
            case SkillSourcesScreen.AddRemoteToken:
                ContinueAddRemoteToken();
                break;
            case SkillSourcesScreen.AddRemoteName:
                SaveNewRemoteSource();
                break;
            case SkillSourcesScreen.RenameSource:
                SaveRename();
                break;
            case SkillSourcesScreen.ChangeLocation:
                SaveLocationChange();
                break;
            case SkillSourcesScreen.RemoveConfirm:
                ActivateRemoveConfirm();
                break;
        }
    }

    public void ToggleSelected()
    {
        if (Screen.Value == SkillSourcesScreen.Inventory)
        {
            var row = GetInventoryRowOrNull();
            if (row?.Action == SkillSourcesInventoryAction.OpenSource && row.SourceKind is { } kind && row.SourceName is { } name)
                ToggleEnabled(kind, name);
            return;
        }

        if (Screen.Value == SkillSourcesScreen.SourceDetail)
        {
            var row = GetDetailRowOrNull();
            if (row?.Action is SkillSourceDetailAction.ToggleEnabled or SkillSourceDetailAction.ToggleSymlinks)
                ActivateDetailRow();
        }
    }

    public void DeleteSelected()
    {
        if (Screen.Value == SkillSourcesScreen.Inventory)
        {
            var row = GetInventoryRowOrNull();
            if (row?.Action == SkillSourcesInventoryAction.OpenSource && row.SourceKind is { } kind && row.SourceName is { } name)
                BeginRemove(kind, name);
            return;
        }

        if (Screen.Value == SkillSourcesScreen.SourceDetail && SelectedSource is { } source)
            BeginRemove(source.Kind, source.Name);
    }

    public void GoBack()
    {
        switch (Screen.Value)
        {
            case SkillSourcesScreen.Inventory:
                RouteRequested?.Invoke("/config");
                Navigate?.Invoke("/config");
                break;
            case SkillSourcesScreen.SourceDetail:
                ShowInventory();
                break;
            case SkillSourcesScreen.AddLocalSymlinks:
                ShowTextScreen(SkillSourcesScreen.AddLocalPath, _pendingLocalPath ?? string.Empty);
                break;
            case SkillSourcesScreen.AddLocalName:
                ShowChoiceScreen(SkillSourcesScreen.AddLocalSymlinks, _pendingLocalAllowSymlinks ? 1 : 0);
                break;
            case SkillSourcesScreen.AddRemoteAuth:
                ShowTextScreen(SkillSourcesScreen.AddRemoteUrl, _pendingRemoteUrl ?? string.Empty);
                break;
            case SkillSourcesScreen.AddRemoteToken:
                if (_editingAction == SkillSourceDetailAction.RotateToken)
                {
                    ShowDetail();
                    break;
                }

                ShowChoiceScreen(SkillSourcesScreen.AddRemoteAuth, _pendingRemoteAuthMode == SkillSourceAuthMode.BearerToken ? 1 : 0);
                break;
            case SkillSourcesScreen.AddRemoteName:
                if (_pendingRemoteAuthMode == SkillSourceAuthMode.BearerToken)
                    ShowTextScreen(SkillSourcesScreen.AddRemoteToken, _pendingRemoteApiKey ?? string.Empty);
                else
                    ShowChoiceScreen(SkillSourcesScreen.AddRemoteAuth, 0);
                break;
            case SkillSourcesScreen.RenameSource:
            case SkillSourcesScreen.ChangeLocation:
            case SkillSourcesScreen.RemoveConfirm:
                ShowDetail();
                break;
            default:
                ClearPendingFlow();
                ShowInventory();
                break;
        }
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    public override void Dispose()
    {
        Screen.Dispose();
        SelectedRow.Dispose();
        Draft.Dispose();
        Version.Dispose();
        Status.Dispose();
        IsSaved.Dispose();
        base.Dispose();
    }

    private void ActivateInventoryRow()
    {
        var row = GetInventoryRowOrNull();
        if (row is null)
            return;

        switch (row.Action)
        {
            case SkillSourcesInventoryAction.OpenSource when row.SourceKind is { } kind && row.SourceName is { } name:
                _selectedKind = kind;
                _selectedName = name;
                ShowDetail();
                break;
            case SkillSourcesInventoryAction.AddLocalFolder:
                BeginAddLocalFolder();
                break;
            case SkillSourcesInventoryAction.AddSkillServer:
                BeginAddRemoteServer();
                break;
            case SkillSourcesInventoryAction.RescanAll:
                RescanAll();
                break;
            case SkillSourcesInventoryAction.Done:
                GoBack();
                break;
        }
    }

    private void ActivateDetailRow()
    {
        if (SelectedSource is not { } source)
        {
            ShowInventory();
            return;
        }

        var row = GetDetailRowOrNull();
        if (row is null)
            return;

        switch (row.Action)
        {
            case SkillSourceDetailAction.ToggleEnabled:
                ToggleEnabled(source.Kind, source.Name);
                break;
            case SkillSourceDetailAction.ToggleSymlinks:
                ToggleLocalSymlinks(source.Name);
                break;
            case SkillSourceDetailAction.Rescan:
            case SkillSourceDetailAction.TestConnection:
                TestSource(source);
                break;
            case SkillSourceDetailAction.Location:
                if (source.Kind == SkillSourceKind.LocalFolder && source.IsWellKnown)
                {
                    SetStatus("Well-known source paths are managed automatically.", ConfigStatusTone.Neutral);
                    break;
                }

                BeginChangeLocation(source);
                break;
            case SkillSourceDetailAction.Rename:
                _editingAction = SkillSourceDetailAction.Rename;
                ShowTextScreen(SkillSourcesScreen.RenameSource, source.Name);
                break;
            case SkillSourceDetailAction.ChangeLocation:
                BeginChangeLocation(source);
                break;
            case SkillSourceDetailAction.SyncInterval:
                CycleRemoteSyncInterval(source.Name);
                break;
            case SkillSourceDetailAction.RotateToken:
                _editingAction = SkillSourceDetailAction.RotateToken;
                ShowTextScreen(SkillSourcesScreen.AddRemoteToken, string.Empty);
                break;
            case SkillSourceDetailAction.RemoveToken:
                RemoveRemoteToken(source.Name);
                break;
            case SkillSourceDetailAction.RemoveSource:
                BeginRemove(source.Kind, source.Name);
                break;
            case SkillSourceDetailAction.Done:
                ShowInventory();
                break;
        }
    }

    private void BeginAddLocalFolder()
    {
        ClearPendingFlow();
        ShowTextScreen(SkillSourcesScreen.AddLocalPath, string.Empty);
    }

    private void ContinueAddLocalPath()
    {
        if (!TryNormalizeExternalDirectory(Draft.Value.Trim(), out var fullPath, out var error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        _pendingLocalPath = fullPath;
        _pendingLocalAllowSymlinks = false;
        ShowChoiceScreen(SkillSourcesScreen.AddLocalSymlinks, 0);
    }

    private void ContinueAddLocalSymlinks()
    {
        _pendingLocalAllowSymlinks = SelectedRow.Value == 1;
        var suggestedName = SuggestNameFromPath(_pendingLocalPath ?? "team-skills");
        ShowTextScreen(SkillSourcesScreen.AddLocalName, MakeUniqueName(suggestedName));
    }

    private void SaveNewLocalSource()
    {
        if (_pendingLocalPath is null)
        {
            SetStatus("Local folder path is required before adding a source.", ConfigStatusTone.Error);
            return;
        }

        var name = NormalizeSourceName(Draft.Value);
        if (!ValidateNewSourceName(name, null, out var error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        var external = LoadExternalConfig();
        external.Sources.Add(new ExternalSkillSource
        {
            Name = name,
            Path = _pendingLocalPath,
            Enabled = true,
            AllowSymlinks = _pendingLocalAllowSymlinks,
        });

        SaveExternalConfig(external);
        ClearPendingFlow();
        ReloadSources();
        _selectedKind = SkillSourceKind.LocalFolder;
        _selectedName = name;
        ShowDetail($"Added local skill folder '{name}'.");
    }

    private void BeginAddRemoteServer()
    {
        ClearPendingFlow();
        ShowTextScreen(SkillSourcesScreen.AddRemoteUrl, string.Empty);
    }

    private void BeginChangeLocation(SkillSourceDisplay source)
    {
        _editingAction = SkillSourceDetailAction.ChangeLocation;
        ShowTextScreen(SkillSourcesScreen.ChangeLocation, source.Location);
    }

    private void ContinueAddRemoteUrl()
    {
        if (!TryNormalizeFeedUrl(Draft.Value.Trim(), out var url, out var error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        _pendingRemoteUrl = url ?? throw new InvalidOperationException("Validated skill server URL was null.");
        _pendingRemoteAuthMode = SkillSourceAuthMode.None;
        _pendingRemoteApiKey = null;
        _pendingRemoteProbeMessage = null;
        _pendingRemoteTimeoutSeconds = DefaultFeedTimeoutSeconds;
        ShowChoiceScreen(SkillSourcesScreen.AddRemoteAuth, 0);
    }

    private void ContinueAddRemoteAuth()
    {
        _pendingRemoteAuthMode = SelectedRow.Value == 1 ? SkillSourceAuthMode.BearerToken : SkillSourceAuthMode.None;
        if (_pendingRemoteAuthMode == SkillSourceAuthMode.BearerToken)
        {
            ShowTextScreen(SkillSourcesScreen.AddRemoteToken, string.Empty);
            return;
        }

        ProbePendingRemoteThenReview();
    }

    private void ContinueAddRemoteToken()
    {
        if (_editingAction == SkillSourceDetailAction.RotateToken)
        {
            SaveRotatedRemoteToken();
            return;
        }

        var token = Draft.Value.Trim();
        if (!TryValidateApiKeyDraft(token, out var error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            SetStatus("Bearer token is required when authentication is set to bearer token.", ConfigStatusTone.Error);
            return;
        }

        _pendingRemoteApiKey = token;
        ProbePendingRemoteThenReview();
    }

    private void ProbePendingRemoteThenReview()
    {
        if (_pendingRemoteUrl is null)
        {
            SetStatus("Skill server URL is required before testing a source.", ConfigStatusTone.Error);
            return;
        }

        var apiKey = _pendingRemoteAuthMode == SkillSourceAuthMode.BearerToken ? _pendingRemoteApiKey : null;
        var fingerprint = $"{_pendingRemoteUrl}|{apiKey?.Length ?? 0}";
        if (_saveAnywayFingerprint != fingerprint)
        {
            var result = _probe.Probe(_pendingRemoteUrl, apiKey, _pendingRemoteTimeoutSeconds);
            _pendingRemoteProbeMessage = result.Message;
            if (!result.Success)
            {
                _saveAnywayFingerprint = fingerprint;
                SetStatus($"{result.Message} Press Enter again to save anyway.", ConfigStatusTone.Warning);
                return;
            }
        }

        var suggestedName = SuggestNameFromUrl(_pendingRemoteUrl);
        ShowTextScreen(SkillSourcesScreen.AddRemoteName, MakeUniqueName(suggestedName));
    }

    private void SaveNewRemoteSource()
    {
        if (_pendingRemoteUrl is null)
        {
            SetStatus("Skill server URL is required before adding a source.", ConfigStatusTone.Error);
            return;
        }

        var name = NormalizeSourceName(Draft.Value);
        if (!ValidateNewSourceName(name, null, out var error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        var feeds = LoadSkillFeedsSection(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));
        feeds.Feeds.Add(new SkillFeedConfigEntry
        {
            Name = name,
            Url = _pendingRemoteUrl,
            Enabled = true,
            TimeoutSeconds = _pendingRemoteTimeoutSeconds,
            ApiKey = _pendingRemoteAuthMode == SkillSourceAuthMode.BearerToken && !string.IsNullOrWhiteSpace(_pendingRemoteApiKey)
                ? ProtectApiKeyForConfig(_paths, _pendingRemoteApiKey)
                : null,
        });

        SaveSkillFeedsConfig(feeds);
        ClearPendingFlow();
        ReloadSources();
        _selectedKind = SkillSourceKind.RemoteSkillServer;
        _selectedName = name;
        ShowDetail($"Added skill server '{name}'.");
    }

    private void ToggleEnabled(SkillSourceKind kind, string name)
    {
        if (kind == SkillSourceKind.LocalFolder)
        {
            var external = LoadExternalConfig();
            var source = FindLocalSource(external, name);
            if (source is null)
            {
                SetStatus($"Local skill folder '{name}' no longer exists in config.", ConfigStatusTone.Error);
                ReloadSources();
                return;
            }

            source.Enabled = !source.Enabled;
            SaveExternalConfig(external);
            ReloadSources();
            SetStatus($"Local skill folder '{name}' {(source.Enabled ? "enabled" : "disabled")}.", ConfigStatusTone.Success);
            return;
        }

        var feeds = LoadSkillFeedsSection(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));
        var feed = FindRemoteSource(feeds, name);
        if (feed is null)
        {
            SetStatus($"Skill server '{name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        feed.Enabled = !feed.Enabled;
        SaveSkillFeedsConfig(feeds);
        ReloadSources();
        SetStatus($"Skill server '{name}' {(feed.Enabled ? "enabled" : "disabled")}.", ConfigStatusTone.Success);
    }

    private void ToggleLocalSymlinks(string name)
    {
        var external = LoadExternalConfig();
        var source = FindLocalSource(external, name);
        if (source is null)
        {
            SetStatus($"Local skill folder '{name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        source.AllowSymlinks = !source.AllowSymlinks;
        SaveExternalConfig(external);
        ReloadSources();
        SetStatus($"Local skill folder '{name}' symlink policy saved.", ConfigStatusTone.Success);
    }

    private void CycleRemoteSyncInterval(string name)
    {
        var feeds = LoadSkillFeedsSection(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));
        var feed = FindRemoteSource(feeds, name);
        if (feed is null)
        {
            SetStatus($"Skill server '{name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        feed.TimeoutSeconds = feed.TimeoutSeconds switch
        {
            <= 10 => 30,
            <= 30 => 60,
            _ => 10,
        };

        SaveSkillFeedsConfig(feeds);
        ReloadSources();
        SetStatus($"Skill server '{name}' timeout saved as {feed.TimeoutSeconds}s.", ConfigStatusTone.Success);
    }

    private void TestSource(SkillSourceDisplay source)
    {
        if (source.Kind == SkillSourceKind.LocalFolder)
        {
            if (Directory.Exists(source.Location))
            {
                var scan = ScanLocalSkills(source.Location, source.AllowSymlinks);
                if (scan.Warning is null)
                {
                    SetStatus($"Local folder '{source.Name}' is readable ({scan.Count} skills discovered).", ConfigStatusTone.Success);
                }
                else
                {
                    SetStatus($"Local folder '{source.Name}' scan warning: {scan.Warning}", ConfigStatusTone.Warning);
                }
            }
            else
            {
                SetStatus($"Local folder '{source.Name}' does not exist: {source.Location}", ConfigStatusTone.Error);
            }

            return;
        }

        var feeds = LoadSkillFeedsSection(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));
        var feed = FindRemoteSource(feeds, source.Name);
        if (feed is null)
        {
            SetStatus($"Skill server '{source.Name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        var apiKey = TryGetFeedApiKeyPlaintext(feed, out var plaintext, out var error) ? plaintext : null;
        if (!string.IsNullOrWhiteSpace(error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        var result = _probe.Probe(feed.Url, apiKey, feed.TimeoutSeconds);
        SetStatus(result.Message, result.Success ? ConfigStatusTone.Success : ConfigStatusTone.Warning);
    }

    private void SaveRename()
    {
        if (SelectedSource is not { } source)
        {
            ShowInventory();
            return;
        }

        var newName = NormalizeSourceName(Draft.Value);
        if (!ValidateNewSourceName(newName, source.Name, out var error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        if (source.Kind == SkillSourceKind.LocalFolder)
        {
            var external = LoadExternalConfig();
            var item = FindLocalSource(external, source.Name);
            if (item is null)
            {
                SetStatus($"Local skill folder '{source.Name}' no longer exists in config.", ConfigStatusTone.Error);
                ReloadSources();
                return;
            }

            item.Name = newName;
            SaveExternalConfig(external);
        }
        else
        {
            var feeds = LoadSkillFeedsSection(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));
            var item = FindRemoteSource(feeds, source.Name);
            if (item is null)
            {
                SetStatus($"Skill server '{source.Name}' no longer exists in config.", ConfigStatusTone.Error);
                ReloadSources();
                return;
            }

            item.Name = newName;
            SaveSkillFeedsConfig(feeds);
        }

        _selectedName = newName;
        ReloadSources();
        ShowDetail($"Renamed source to '{newName}'.");
    }

    private void SaveLocationChange()
    {
        if (SelectedSource is not { } source)
        {
            ShowInventory();
            return;
        }

        if (source.Kind == SkillSourceKind.LocalFolder)
        {
            SaveLocalPathChange(source);
            return;
        }

        SaveRemoteUrlChange(source);
    }

    private void SaveLocalPathChange(SkillSourceDisplay source)
    {
        if (source.IsWellKnown)
        {
            SetStatus("Well-known source paths are managed automatically.", ConfigStatusTone.Error);
            return;
        }

        if (!TryNormalizeExternalDirectory(Draft.Value.Trim(), out var fullPath, out var error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        var external = LoadExternalConfig();
        var item = FindLocalSource(external, source.Name);
        if (item is null)
        {
            SetStatus($"Local skill folder '{source.Name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        item.Path = fullPath;
        SaveExternalConfig(external);
        ReloadSources();
        ShowDetail($"Local skill folder '{source.Name}' path saved.");
    }

    private void SaveRemoteUrlChange(SkillSourceDisplay source)
    {
        if (!TryNormalizeFeedUrl(Draft.Value.Trim(), out var url, out var error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        var normalizedUrl = url ?? throw new InvalidOperationException("Validated skill server URL was null.");

        var feeds = LoadSkillFeedsSection(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));
        var item = FindRemoteSource(feeds, source.Name);
        if (item is null)
        {
            SetStatus($"Skill server '{source.Name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        var apiKey = TryGetFeedApiKeyPlaintext(item, out var plaintext, out var decryptError) ? plaintext : null;
        if (!string.IsNullOrWhiteSpace(decryptError))
        {
            SetStatus(decryptError, ConfigStatusTone.Error);
            return;
        }

        var fingerprint = $"change-url|{source.Name}|{normalizedUrl}|{apiKey?.Length ?? 0}";
        if (_saveAnywayFingerprint != fingerprint)
        {
            var probeResult = _probe.Probe(normalizedUrl, apiKey, item.TimeoutSeconds);
            if (!probeResult.Success)
            {
                _saveAnywayFingerprint = fingerprint;
                SetStatus($"{probeResult.Message} Press Enter again to save anyway.", ConfigStatusTone.Warning);
                return;
            }
        }

        item.Url = normalizedUrl;
        SaveSkillFeedsConfig(feeds);
        _saveAnywayFingerprint = null;
        ReloadSources();
        ShowDetail($"Skill server '{source.Name}' URL saved.");
    }

    private void SaveRotatedRemoteToken()
    {
        if (SelectedSource is not { Kind: SkillSourceKind.RemoteSkillServer } source)
        {
            ShowInventory();
            return;
        }

        var token = Draft.Value.Trim();
        if (!TryValidateApiKeyDraft(token, out var error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            SetStatus("New bearer token is required. Use Remove token to delete an existing token.", ConfigStatusTone.Error);
            return;
        }

        var feeds = LoadSkillFeedsSection(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));
        var feed = FindRemoteSource(feeds, source.Name);
        if (feed is null)
        {
            SetStatus($"Skill server '{source.Name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        var fingerprint = $"rotate-token|{source.Name}|{feed.Url}|{token.Length}";
        if (_saveAnywayFingerprint != fingerprint)
        {
            var probeResult = _probe.Probe(feed.Url, token, feed.TimeoutSeconds);
            if (!probeResult.Success)
            {
                _saveAnywayFingerprint = fingerprint;
                SetStatus($"{probeResult.Message} Press Enter again to save anyway.", ConfigStatusTone.Warning);
                return;
            }
        }

        feed.ApiKey = ProtectApiKeyForConfig(_paths, token);
        SaveSkillFeedsConfig(feeds);
        _saveAnywayFingerprint = null;
        _editingAction = null;
        ReloadSources();
        ShowDetail($"Skill server '{source.Name}' token rotated.");
    }

    private void RemoveRemoteToken(string name)
    {
        var feeds = LoadSkillFeedsSection(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));
        var feed = FindRemoteSource(feeds, name);
        if (feed is null)
        {
            SetStatus($"Skill server '{name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        if (string.IsNullOrWhiteSpace(feed.ApiKey))
        {
            SetStatus($"Skill server '{name}' has no token to remove.", ConfigStatusTone.Neutral);
            return;
        }

        feed.ApiKey = null;
        SaveSkillFeedsConfig(feeds);
        ReloadSources();
        SetStatus($"Skill server '{name}' token removed.", ConfigStatusTone.Success);
    }

    private void BeginRemove(SkillSourceKind kind, string name)
    {
        _selectedKind = kind;
        _selectedName = name;
        ShowChoiceScreen(SkillSourcesScreen.RemoveConfirm, 0);
    }

    private void ActivateRemoveConfirm()
    {
        if (SelectedRow.Value == 0)
        {
            ShowDetail();
            return;
        }

        if (_selectedKind is not { } kind || _selectedName is not { } name)
        {
            ShowInventory();
            return;
        }

        if (kind == SkillSourceKind.LocalFolder)
        {
            var external = LoadExternalConfig();
            external.Sources.RemoveAll(s => _nameComparer.Equals(s.Name, name));
            SaveExternalConfig(external);
        }
        else
        {
            var feeds = LoadSkillFeedsSection(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));
            feeds.Feeds.RemoveAll(f => _nameComparer.Equals(f.Name, name));
            SaveSkillFeedsConfig(feeds);
        }

        _selectedKind = null;
        _selectedName = null;
        ReloadSources();
        ShowInventory($"Removed skill source '{name}'.");
    }

    private void RescanAll()
    {
        ReloadSources();
        var localCount = _sources.Count(s => s.Kind == SkillSourceKind.LocalFolder);
        var remoteCount = _sources.Count(s => s.Kind == SkillSourceKind.RemoteSkillServer);
        SetStatus($"Rescanned {localCount} local folder(s) and {remoteCount} skill server(s).", ConfigStatusTone.Success);
    }

    private IReadOnlyList<SkillSourcesInventoryRow> BuildInventoryRows()
    {
        var rows = new List<SkillSourcesInventoryRow>();
        foreach (var source in _sources)
        {
            rows.Add(new SkillSourcesInventoryRow(
                SkillSourcesInventoryAction.OpenSource,
                source.Kind,
                source.Name,
                FormatSourceLabel(source),
                source.StatusText,
                source.StatusTone));
        }

        rows.Add(new SkillSourcesInventoryRow(SkillSourcesInventoryAction.AddLocalFolder, null, null, "+ Add local folder", "Scan a directory on this machine.", ConfigStatusTone.Neutral));
        rows.Add(new SkillSourcesInventoryRow(SkillSourcesInventoryAction.AddSkillServer, null, null, "+ Add skill server", "Connect to a remote skill feed.", ConfigStatusTone.Neutral));
        rows.Add(new SkillSourcesInventoryRow(SkillSourcesInventoryAction.RescanAll, null, null, "Rescan all", "Refresh local source status.", ConfigStatusTone.Neutral));
        rows.Add(new SkillSourcesInventoryRow(SkillSourcesInventoryAction.Done, null, null, "Done", "Return to Settings Areas.", ConfigStatusTone.Neutral));
        return rows;
    }

    private IReadOnlyList<SkillSourceDetailRow> BuildDetailRows(SkillSourceDisplay source)
    {
        if (source.Kind == SkillSourceKind.LocalFolder)
        {
            var rows = new List<SkillSourceDetailRow>
            {
                new(SkillSourceDetailAction.ToggleEnabled, $"Enabled                 [{Check(source.Enabled)}]", "Autosaves source enabled state.", ConfigStatusTone.Neutral),
                new(SkillSourceDetailAction.Location, $"Path                    {source.Location}", source.IsWellKnown ? "Well-known path is managed automatically." : "Enter to change path.", ConfigStatusTone.Neutral),
                new(SkillSourceDetailAction.ToggleSymlinks, $"Allow symlinks          [{Check(source.AllowSymlinks)}]", "Autosaves symlink policy.", ConfigStatusTone.Neutral),
                new(SkillSourceDetailAction.Rescan, "Rescan folder", "Check readability and discovered skill count.", ConfigStatusTone.Neutral),
                new(SkillSourceDetailAction.Rename, "Rename source", "Change the display/config name.", ConfigStatusTone.Neutral),
            };

            if (!source.IsWellKnown)
                rows.Add(new SkillSourceDetailRow(SkillSourceDetailAction.ChangeLocation, "Change path", "Validate and save a new local directory.", ConfigStatusTone.Neutral));

            rows.Add(new SkillSourceDetailRow(SkillSourceDetailAction.RemoveSource, "Remove source", "Stop loading skills from this folder.", ConfigStatusTone.Warning));
            rows.Add(new SkillSourceDetailRow(SkillSourceDetailAction.Done, "Done", "Return to Skill Sources.", ConfigStatusTone.Neutral));
            return rows;
        }

        return
        [
            new(SkillSourceDetailAction.ToggleEnabled, $"Enabled                 [{Check(source.Enabled)}]", "Autosaves source enabled state.", ConfigStatusTone.Neutral),
            new(SkillSourceDetailAction.Location, $"URL                     {source.Location}", "Enter to change URL and test discovery.", ConfigStatusTone.Neutral),
            new(SkillSourceDetailAction.Authentication, $"Authentication          {(source.HasApiKey ? "bearer token configured" : "none")}", "Use Rotate token or Remove token for credentials.", ConfigStatusTone.Neutral),
            new(SkillSourceDetailAction.SyncInterval, $"HTTP timeout             {source.TimeoutSeconds}s", "Enter to cycle 10s / 30s / 60s.", ConfigStatusTone.Neutral),
            new(SkillSourceDetailAction.TestConnection, "Test connection", "Probe the discovery endpoint.", ConfigStatusTone.Neutral),
            new(SkillSourceDetailAction.Rename, "Rename source", "Change the display/config name.", ConfigStatusTone.Neutral),
            new(SkillSourceDetailAction.ChangeLocation, "Change URL", "Validate and save a new server URL.", ConfigStatusTone.Neutral),
            new(SkillSourceDetailAction.RotateToken, "Rotate token", "Replace the stored bearer token.", ConfigStatusTone.Neutral),
            new(SkillSourceDetailAction.RemoveToken, "Remove token", "Delete the stored bearer token.", ConfigStatusTone.Warning),
            new(SkillSourceDetailAction.RemoveSource, "Remove source", "Stop loading skills from this server.", ConfigStatusTone.Warning),
            new(SkillSourceDetailAction.Done, "Done", "Return to Skill Sources.", ConfigStatusTone.Neutral),
        ];
    }

    private void ReloadSources()
    {
        var root = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        var external = LoadSection<ExternalSkillsConfig>(root, "ExternalSkills");
        var feeds = LoadSkillFeedsSection(root);
        _sources = BuildSources(external, feeds).ToList();
        Version.Value++;
    }

    private IEnumerable<SkillSourceDisplay> BuildSources(ExternalSkillsConfig external, SkillFeedsConfigDocument feeds)
    {
        foreach (var source in external.Sources.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var location = ResolveLocalDisplayPath(source);
            var exists = Directory.Exists(location);
            var scan = exists ? ScanLocalSkills(location, source.AllowSymlinks) : null;
            var hasScanWarning = scan?.Warning is not null;
            yield return new SkillSourceDisplay(
                SkillSourceKind.LocalFolder,
                source.Name,
                location,
                source.Enabled,
                !string.IsNullOrWhiteSpace(source.WellKnown),
                source.AllowSymlinks,
                false,
                DefaultFeedTimeoutSeconds,
                exists ? hasScanWarning ? $"scan warning ({scan!.Count} skill{Plural(scan.Count)})" : $"{scan!.Count} skill{Plural(scan.Count)}" : "missing folder",
                exists && !hasScanWarning ? ConfigStatusTone.Success : ConfigStatusTone.Warning);
        }

        foreach (var feed in feeds.Feeds.OrderBy(static f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return new SkillSourceDisplay(
                SkillSourceKind.RemoteSkillServer,
                feed.Name,
                feed.Url,
                feed.Enabled,
                false,
                false,
                !string.IsNullOrWhiteSpace(feed.ApiKey),
                feed.TimeoutSeconds,
                string.IsNullOrWhiteSpace(feed.ApiKey) ? "no auth" : "token configured",
                ConfigStatusTone.Neutral);
        }
    }

    private int RowCountForCurrentScreen()
        => Screen.Value switch
        {
            SkillSourcesScreen.Inventory => InventoryRows.Count,
            SkillSourcesScreen.SourceDetail => DetailRows.Count,
            SkillSourcesScreen.AddLocalSymlinks => 2,
            SkillSourcesScreen.AddRemoteAuth => 2,
            SkillSourcesScreen.RemoveConfirm => 2,
            _ => 1,
        };

    private SkillSourcesInventoryRow? GetInventoryRowOrNull()
    {
        var rows = InventoryRows;
        return SelectedRow.Value >= 0 && SelectedRow.Value < rows.Count ? rows[SelectedRow.Value] : null;
    }

    private SkillSourceDetailRow? GetDetailRowOrNull()
    {
        var rows = DetailRows;
        return SelectedRow.Value >= 0 && SelectedRow.Value < rows.Count ? rows[SelectedRow.Value] : null;
    }

    private void ShowInventory(string? message = null)
    {
        Screen.Value = SkillSourcesScreen.Inventory;
        SelectedRow.Value = Math.Clamp(SelectedRow.Value, 0, Math.Max(0, InventoryRows.Count - 1));
        Draft.Value = string.Empty;
        _editingAction = null;
        if (message is not null)
            SetStatus(message, ConfigStatusTone.Success);
        else
            RequestRedraw();
    }

    private void ShowDetail(string? message = null)
    {
        Screen.Value = SkillSourcesScreen.SourceDetail;
        SelectedRow.Value = 0;
        Draft.Value = string.Empty;
        _editingAction = null;
        if (message is not null)
            SetStatus(message, ConfigStatusTone.Success);
        else
            RequestRedraw();
    }

    private void ShowTextScreen(SkillSourcesScreen screen, string seed)
    {
        Screen.Value = screen;
        SelectedRow.Value = 0;
        Draft.Value = seed;
        ClearStatus();
        RequestRedraw();
    }

    private void ShowChoiceScreen(SkillSourcesScreen screen, int row)
    {
        Screen.Value = screen;
        SelectedRow.Value = row;
        Draft.Value = string.Empty;
        ClearStatus();
        RequestRedraw();
    }

    private void MarkDirty()
    {
        IsSaved.Value = false;
        _saveAnywayFingerprint = null;
        ClearStatus();
        RequestRedraw();
    }

    private void SetStatus(string message, ConfigStatusTone tone)
    {
        Status.Value = new ConfigStatusMessage(message, tone);
        IsSaved.Value = tone == ConfigStatusTone.Success;
        RequestRedraw();
    }

    private void ClearStatus()
    {
        if (!string.IsNullOrWhiteSpace(Status.Value.Text))
            Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
    }

    private void ClearPendingFlow()
    {
        _pendingLocalPath = null;
        _pendingLocalAllowSymlinks = false;
        _pendingRemoteUrl = null;
        _pendingRemoteAuthMode = SkillSourceAuthMode.None;
        _pendingRemoteApiKey = null;
        _pendingRemoteProbeMessage = null;
        _pendingRemoteTimeoutSeconds = DefaultFeedTimeoutSeconds;
        _saveAnywayFingerprint = null;
        _editingAction = null;
        Draft.Value = string.Empty;
    }

    private bool ValidateNewSourceName(string name, string? currentName, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Source name is required.";
            return false;
        }

        var duplicate = _sources.Any(source => !_nameComparer.Equals(source.Name, currentName) && _nameComparer.Equals(source.Name, name));
        if (duplicate)
        {
            error = $"A skill source named '{name}' already exists.";
            return false;
        }

        return true;
    }

    private bool TryGetFeedApiKeyPlaintext(SkillFeedConfigEntry feed, out string? plaintext, out string error)
    {
        plaintext = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(feed.ApiKey))
            return true;

        if (!TryDecryptExistingApiKey(_paths, feed.ApiKey, out plaintext, out error))
            return false;

        return true;
    }

    private ExternalSkillsConfig LoadExternalConfig()
        => LoadSection<ExternalSkillsConfig>(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath), "ExternalSkills");

    private void SaveExternalConfig(ExternalSkillsConfig external)
    {
        var root = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        root["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        if (external.Sources.Count == 0)
            root.Remove("ExternalSkills");
        else
            root["ExternalSkills"] = BuildExternalSkillsSection(external);

        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, root);
    }

    private void SaveSkillFeedsConfig(SkillFeedsConfigDocument feeds)
    {
        var root = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        root["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        if (feeds.Feeds.Count == 0)
            root.Remove("SkillFeeds");
        else
            root["SkillFeeds"] = BuildSkillFeedsSection(feeds);

        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, root);
    }

    private ExternalSkillSource? FindLocalSource(ExternalSkillsConfig external, string name)
        => external.Sources.FirstOrDefault(source => _nameComparer.Equals(source.Name, name));

    private SkillFeedConfigEntry? FindRemoteSource(SkillFeedsConfigDocument feeds, string name)
        => feeds.Feeds.FirstOrDefault(feed => _nameComparer.Equals(feed.Name, name));

    private static bool IsTextEntryScreen(SkillSourcesScreen screen)
        => screen is SkillSourcesScreen.AddLocalPath
            or SkillSourcesScreen.AddLocalName
            or SkillSourcesScreen.AddRemoteUrl
            or SkillSourcesScreen.AddRemoteToken
            or SkillSourcesScreen.AddRemoteName
            or SkillSourcesScreen.RenameSource
            or SkillSourcesScreen.ChangeLocation;

    private static string FormatSourceLabel(SkillSourceDisplay source)
    {
        var kind = source.Kind == SkillSourceKind.LocalFolder ? "local" : "server";
        var enabled = source.Enabled ? "x" : " ";
        return $"[{enabled}] {source.Name,-18} {kind,-6} {TruncateMiddle(source.Location, 38)}";
    }

    private static string ResolveLocalDisplayPath(ExternalSkillSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.Path))
            return source.Path;

        if (!string.IsNullOrWhiteSpace(source.WellKnown))
            return ExternalSkillsConfig.ResolveWellKnownPath(source.WellKnown) ?? source.WellKnown;

        return "(unresolved)";
    }

    private static bool TryNormalizeExternalDirectory(string value, out string? fullPath, out string error)
    {
        fullPath = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Local skill folder path is required.";
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            error = "Local skill folder must be a local filesystem path, not a URL.";
            return false;
        }

        try
        {
            var expanded = PathExpansion.ExpandHome(value) ?? value;
            fullPath = Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Local skill folder is not a valid path: {ex.Message}";
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            error = "Local skill folder must already exist so runtime skill scanning can consume it.";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeFeedUrl(string value, out string? url, out string error)
    {
        url = null;
        error = string.Empty;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            error = "Skill server URL must be an absolute HTTP or HTTPS URI.";
            return false;
        }

        url = uri.ToString().TrimEnd('/');
        return true;
    }

    private static bool TryValidateApiKeyDraft(string value, out string error)
    {
        error = string.Empty;
        if (value.Contains('\r') || value.Contains('\n'))
        {
            error = "Skill server bearer token must be a single-line value.";
            return false;
        }

        return true;
    }

    private static T LoadSection<T>(Dictionary<string, object> root, string sectionName) where T : new()
    {
        if (!root.TryGetValue(sectionName, out var raw) || raw is null)
            return new T();

        var json = raw is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(raw, JsonDefaults.ConfigFile);
        return JsonSerializer.Deserialize<T>(json, JsonDefaults.ConfigRead) ?? new T();
    }

    private static Dictionary<string, object> BuildExternalSkillsSection(ExternalSkillsConfig config)
        => new()
        {
            ["Sources"] = config.Sources.Select(static source =>
            {
                var item = new Dictionary<string, object>
                {
                    ["Name"] = source.Name,
                    ["Enabled"] = source.Enabled,
                    ["AllowSymlinks"] = source.AllowSymlinks,
                };

                if (!string.IsNullOrWhiteSpace(source.WellKnown))
                    item["WellKnown"] = source.WellKnown;
                if (!string.IsNullOrWhiteSpace(source.Path))
                    item["Path"] = source.Path;

                return (object)item;
            }).ToArray(),
        };

    private static SkillFeedsConfigDocument LoadSkillFeedsSection(Dictionary<string, object> root)
    {
        if (!root.TryGetValue("SkillFeeds", out var raw) || raw is null)
            return new SkillFeedsConfigDocument();

        var json = raw is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(raw, JsonDefaults.ConfigFile);
        return JsonSerializer.Deserialize<SkillFeedsConfigDocument>(json, JsonDefaults.ConfigRead) ?? new SkillFeedsConfigDocument();
    }

    private static bool TryDecryptExistingApiKey(NetclawPaths paths, string apiKey, out string? plaintext, out string error)
    {
        plaintext = null;
        error = string.Empty;

        if (!ISecretsProtector.IsEncrypted(apiKey))
        {
            plaintext = apiKey;
            return true;
        }

        try
        {
            plaintext = SecretsProtection.CreateProtector(paths).Unprotect(apiKey);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            error = $"Existing skill server token could not be decrypted: {ex.Message}";
            return false;
        }

        return true;
    }

    private static string ProtectApiKeyForConfig(NetclawPaths paths, string apiKey)
        => SecretsProtection.CreateProtector(paths).Protect(apiKey);

    private static Dictionary<string, object> BuildSkillFeedsSection(SkillFeedsConfigDocument config)
        => new()
        {
            ["SyncIntervalMinutes"] = config.SyncIntervalMinutes,
            ["Feeds"] = config.Feeds.Select(static feed =>
            {
                var item = new Dictionary<string, object>
                {
                    ["Name"] = feed.Name,
                    ["Url"] = feed.Url,
                    ["Enabled"] = feed.Enabled,
                    ["TimeoutSeconds"] = feed.TimeoutSeconds,
                };

                if (!string.IsNullOrWhiteSpace(feed.ApiKey))
                    item["ApiKey"] = feed.ApiKey;

                return (object)item;
            }).ToArray(),
        };

    private static LocalSkillScanDisplay ScanLocalSkills(string directory, bool allowSymlinks)
    {
        try
        {
            var result = SkillScanner.Scan(directory, allowSymlinks, strictNameMatch: false);
            if (result.Issues.Count == 0)
                return new LocalSkillScanDisplay(result.AcceptedSkills.Count, null);

            var firstIssue = result.Issues[0];
            return new LocalSkillScanDisplay(result.AcceptedSkills.Count, $"{firstIssue.Kind}: {firstIssue.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return new LocalSkillScanDisplay(0, ex.Message);
        }
    }

    private static string SuggestNameFromPath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return NormalizeSourceName(string.IsNullOrWhiteSpace(name) ? "local-skills" : name);
    }

    private static string SuggestNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return NormalizeSourceName(uri.Host);
        }
        catch
        {
            return "custom-feed";
        }
    }

    private string MakeUniqueName(string seed)
    {
        var baseName = NormalizeSourceName(seed);
        if (!_sources.Any(source => _nameComparer.Equals(source.Name, baseName)))
            return baseName;

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!_sources.Any(source => _nameComparer.Equals(source.Name, candidate)))
                return candidate;
        }

        return $"{baseName}-{Guid.NewGuid():N}"[..Math.Min(baseName.Length + 9, 32)];
    }

    private static string NormalizeSourceName(string value)
    {
        var chars = new List<char>(value.Length);
        var previousWasHyphen = false;
        foreach (var c in value.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                chars.Add(char.ToLowerInvariant(c));
                previousWasHyphen = false;
            }
            else if (!previousWasHyphen)
            {
                chars.Add('-');
                previousWasHyphen = true;
            }
        }

        var normalized = new string(chars.ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "custom-source" : normalized;
    }

    private static string TruncateMiddle(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        var keep = (maxLength - 3) / 2;
        return value[..keep] + "..." + value[^keep..];
    }

    private static string Check(bool value) => value ? "x" : " ";

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private sealed class SkillFeedsConfigDocument
    {
        public int SyncIntervalMinutes { get; set; } = 60;

        public List<SkillFeedConfigEntry> Feeds { get; set; } = [];
    }

    private sealed class SkillFeedConfigEntry
    {
        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string? ApiKey { get; set; }

        public bool Enabled { get; set; } = true;

        public int TimeoutSeconds { get; set; } = DefaultFeedTimeoutSeconds;
    }
}

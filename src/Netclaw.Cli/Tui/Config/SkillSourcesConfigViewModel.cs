// -----------------------------------------------------------------------
// <copyright file="SkillSourcesConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Netclaw.Actors.Skills;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using R3;
using Termina.Layout;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

internal sealed record SkillFeedReachabilityResult(bool Success, string Message, bool RequiresAuth = false, int? SkillCount = null);

internal interface ISkillFeedReachabilityProbe
{
    Task<SkillFeedReachabilityResult> ProbeAsync(string baseUrl, string? apiKey, int timeoutSeconds, CancellationToken ct = default);
}

internal sealed class SkillFeedReachabilityProbe : ISkillFeedReachabilityProbe
{
    public async Task<SkillFeedReachabilityResult> ProbeAsync(
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        try
        {
            // Ceiling raised to 60s so the background Rescan all count refresh can honor a feed's configured
            // timeout (daemon default 30s); interactive add/test callers cap their own input at 10s for snappy
            // feedback (see StartBackgroundProbe).
            var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60));
            // Link the caller's token to the per-probe timeout so a superseded/abandoned probe
            // (caller cancels via ct) and a slow server (timeout) both unwind the same way.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var client = new HttpClient { Timeout = timeout };
            var root = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(new Uri(root), ".well-known/agent-skills/index.json"));

            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                var skillCount = await CountSkillsAsync(response, cts.Token);
                // "advertised", not "available/loaded": this is the count the server publishes in its index;
                // the daemon may load fewer after content-scan / hash / version filtering.
                var message = skillCount is { } n
                    ? $"Skill feed reachable — {n} skill{(n == 1 ? "" : "s")} advertised."
                    : "Skill feed discovery endpoint is reachable.";
                return new SkillFeedReachabilityResult(true, message, SkillCount: skillCount);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new SkillFeedReachabilityResult(false, $"Skill feed authentication failed with HTTP {(int)response.StatusCode}.", RequiresAuth: true);

            return new SkillFeedReachabilityResult(false, $"Skill feed probe returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER cancelled (probe superseded or abandoned). Surface this to RunProbeAsync as
            // a cancellation so it drops the result quietly, rather than masquerading as a failure.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            // Network/parse/timeout error (NOT caller cancellation): a real, reportable failure.
            return new SkillFeedReachabilityResult(false, $"Skill feed probe failed: {ex.Message}");
        }
    }

    // The discovery index is the RFC agent-skills index ({ "skills": [...] }); count its entries so the
    // operator sees how many skills the server actually serves. Best-effort: reachability already succeeded,
    // so a malformed/slow body just leaves the count unknown (null) rather than failing the probe.
    private static async Task<int?> CountSkillsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            return CountSkillsInIndexJson(json);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    // Count the entries in the RFC agent-skills index ({ "skills": [...] }). Returns null for a body that
    // is not a JSON object with a "skills" array, so a malformed feed leaves the count unknown, not zero.
    internal static int? CountSkillsInIndexJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("skills", out var skills)
                   && skills.ValueKind == JsonValueKind.Array
                ? skills.GetArrayLength()
                : null;
        }
        catch (JsonException)
        {
            return null;
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
    ConfigStatusTone StatusTone,
    // True when a remote feed's stored token is present but NOT ENC:-encrypted (a hand-edited or
    // migrated config). The editor surfaces this as a warning rather than silently using the
    // unprotected credential — CLAUDE.md forbids silent fallbacks on security paths.
    bool ApiKeyIsPlaintext = false);

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

internal sealed record SkillSourceActionTarget(SkillSourceKind Kind, string Name);

internal sealed record LocalSkillScanDisplay(int Count, string? Warning);

/// <summary>
/// Lightweight result of a Skill Sources field commit attempt. Mirrors the inline
/// validation pattern used by the Search config editor: structural failures carry an
/// error tone, reachability-probe failures carry a warning tone that the page surfaces
/// as a "save anyway" override dialog.
/// </summary>
internal sealed record SkillSourceCommitResult(bool Success, string Message, ConfigStatusTone Tone)
{
    public static SkillSourceCommitResult Ok(string message = "")
        => new(true, message, ConfigStatusTone.Success);

    public static SkillSourceCommitResult Failed(string message)
        => new(false, message, ConfigStatusTone.Error);

    public static SkillSourceCommitResult Warning(string message)
        => new(false, message, ConfigStatusTone.Warning);
}

internal sealed class SkillSourcesConfigViewModel : ReactiveViewModel
{
    private const int DefaultFeedTimeoutSeconds = 30;

    // Interactive add/test probes cap their wait here so reachability feedback stays snappy even for a slow
    // server; the background Rescan all count refresh deliberately uses the feed's full configured timeout.
    private const int InteractiveProbeTimeoutSeconds = 10;

    private readonly NetclawPaths _paths;
    private readonly ISkillFeedReachabilityProbe _probe;
    private readonly StringComparer _nameComparer = StringComparer.OrdinalIgnoreCase;
    private CancellationTokenSource? _probeCts;
    private Task? _probeTask;
    private SkillFeedReachabilityResult? _pendingRemoteProbeResult;
    // Off-loop refresh (Rescan all) of per-server skill counts, cached by source name for the inventory row
    // Detail. ConcurrentDictionary because thread-pool probe continuations write it while the loop thread
    // reads it during render; OrdinalIgnoreCase to match the source-name identity used everywhere else.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _remoteSkillCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _countRefreshCts;
    private Task? _countRefreshTask;
    private string? _lastProbeFingerprint;
    private List<SkillSourceDisplay> _sources = [];
    private SkillSourceKind? _selectedKind;
    private string? _selectedName;
    private string? _pendingLocalPath;
    private bool _pendingLocalAllowSymlinks;
    private string? _pendingRemoteUrl;
    private SkillSourceAuthMode _pendingRemoteAuthMode;
    private string? _pendingRemoteApiKey;
    private string? _pendingRemoteProbeMessage;
    private bool _pendingRemoteProbeMessageIsSuccess;
    private int _pendingRemoteTimeoutSeconds = DefaultFeedTimeoutSeconds;
    private SkillSourceDetailAction? _editingAction;
    private SkillSourcesScreen? _validationEditScreen;
    private string? _validationEditDraft;

    public SkillSourcesConfigViewModel(
        NetclawPaths paths,
        ISkillFeedReachabilityProbe? probe = null,
        IFileSystemProvider? fileSystemProvider = null)
    {
        _paths = paths;
        _probe = probe ?? new SkillFeedReachabilityProbe();
        FileSystemProvider = fileSystemProvider ?? new DefaultFileSystemProvider();
        Screen = new ReactiveProperty<SkillSourcesScreen>(SkillSourcesScreen.Inventory);
        SelectedRow = new ReactiveProperty<int>(0);
        Draft = new ReactiveProperty<string>(string.Empty);
        Version = new ReactiveProperty<int>(0);
        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        ActiveValidationDialog = new ReactiveProperty<NetclawValidationDialogModel?>(null);
        IsSaved = new ReactiveProperty<bool>(false);
        ReloadSources();
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    /// <summary>Filesystem access for the "add local folder" directory picker (fakeable in tests).</summary>
    public IFileSystemProvider FileSystemProvider { get; }

    /// <summary>
    /// Directory the "add local folder" picker opens at — the netclaw user's home directory. The
    /// picker can navigate up to the filesystem root and back down, so this is only an anchor.
    /// </summary>
    public string BrowseStartPath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Creates <paramref name="name"/> as a new directory under <paramref name="parentPath"/> and
    /// commits it as the chosen local skill folder. Surfaces a status error on bad input/IO and
    /// leaves the picker open. This is the inline "new folder" affordance the picker itself lacks.
    /// </summary>
    public void CreateAndSelectFolder(string parentPath, string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SetStatus("Enter a valid folder name (no path separators).", ConfigStatusTone.Error);
            return;
        }

        string created;
        try
        {
            created = Path.Combine(parentPath, trimmed);
            Directory.CreateDirectory(created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetStatus($"Could not create folder: {ex.Message}", ConfigStatusTone.Error);
            return;
        }

        CommitAddLocalPath(created);
    }

    public ReactiveProperty<SkillSourcesScreen> Screen { get; }
    public ReactiveProperty<int> SelectedRow { get; }
    public ReactiveProperty<string> Draft { get; }
    public ReactiveProperty<int> Version { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<NetclawValidationDialogModel?> ActiveValidationDialog { get; }
    public ReactiveProperty<bool> IsSaved { get; }

    public IReadOnlyList<SkillSourceDisplay> Sources => _sources;

    public SkillSourceDisplay? SelectedSource => _selectedKind is { } kind && _selectedName is { Length: > 0 } name
        ? _sources.FirstOrDefault(s => s.Kind == kind && _nameComparer.Equals(s.Name, name))
        : null;

    public IReadOnlyList<SkillSourcesInventoryRow> InventoryRows => BuildInventoryRows();

    public IReadOnlyList<SkillSourceDetailRow> DetailRows => SelectedSource is { } source
        ? BuildDetailRows(source)
        : [];

    internal SkillSourcesInventoryRow? CurrentInventoryRow => GetInventoryRowOrNull();

    internal SkillSourceDetailRow? CurrentDetailRow => GetDetailRowOrNull();

    public bool IsTextEntryActive => IsTextEntryScreen(Screen.Value);

    public string CurrentTitle => Screen.Value switch
    {
        SkillSourcesScreen.Inventory => "Skill Sources",
        SkillSourcesScreen.SourceDetail when SelectedSource is { } source => $"Skill Sources > {source.Name}",
        SkillSourcesScreen.AddLocalPath => "Add Local Skill Folder",
        SkillSourcesScreen.AddLocalSymlinks => "Local Folder Security",
        SkillSourcesScreen.AddLocalName => "Review Local Folder",
        SkillSourcesScreen.AddRemoteUrl => "Add Skill Server",
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

    internal void ReplaceDraft(string value)
    {
        if (!IsTextEntryScreen(Screen.Value))
            return;

        Draft.Value = value;
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

    internal SkillSourceActionTarget? ReadCurrentSourceActionTarget()
    {
        if (Screen.Value == SkillSourcesScreen.Inventory)
        {
            var row = GetInventoryRowOrNull();
            return row?.Action == SkillSourcesInventoryAction.OpenSource && row.SourceKind is { } kind && row.SourceName is { } name
                ? new SkillSourceActionTarget(kind, name)
                : null;
        }

        if (SelectedSource is { } source)
            return new SkillSourceActionTarget(source.Kind, source.Name);

        return _selectedKind is { } selectedKind && _selectedName is { Length: > 0 } selectedName
            ? new SkillSourceActionTarget(selectedKind, selectedName)
            : null;
    }

    internal SkillSourceCommitResult ValidateSourceActionTarget(SkillSourceActionTarget? target)
    {
        if (target is null)
            return SkillSourceCommitResult.Failed("A skill source must be selected before changing it.");

        return _sources.Any(source => source.Kind == target.Kind && _nameComparer.Equals(source.Name, target.Name))
            ? SkillSourceCommitResult.Ok()
            : SkillSourceCommitResult.Failed($"Skill source '{target.Name}' no longer exists in config.");
    }

    internal SkillSourceCommitResult ValidateLocalSourceActionTarget(SkillSourceActionTarget? target)
    {
        var validation = ValidateSourceActionTarget(target);
        if (!validation.Success)
            return validation;

        return target!.Kind == SkillSourceKind.LocalFolder
            ? SkillSourceCommitResult.Ok()
            : SkillSourceCommitResult.Failed("A local skill folder must be selected before changing symlink policy.");
    }

    internal SkillSourceCommitResult ValidateRemoteSourceActionTarget(SkillSourceActionTarget? target)
    {
        var validation = ValidateSourceActionTarget(target);
        if (!validation.Success)
            return validation;

        return target!.Kind == SkillSourceKind.RemoteSkillServer
            ? SkillSourceCommitResult.Ok()
            : SkillSourceCommitResult.Failed("A remote skill server must be selected before changing remote settings.");
    }

    internal void CommitToggleEnabled(SkillSourceActionTarget? target)
    {
        if (target is null)
        {
            SetStatus("A skill source must be selected before changing it.", ConfigStatusTone.Error);
            return;
        }

        ToggleEnabled(target.Kind, target.Name);
    }

    internal void CommitToggleLocalSymlinks(SkillSourceActionTarget? target)
    {
        if (target is null)
        {
            SetStatus("A local skill folder must be selected before changing symlink policy.", ConfigStatusTone.Error);
            return;
        }

        ToggleLocalSymlinks(target.Name);
    }

    internal void CommitCycleRemoteSyncInterval(SkillSourceActionTarget? target)
    {
        if (target is null)
        {
            SetStatus("A remote skill server must be selected before changing timeout.", ConfigStatusTone.Error);
            return;
        }

        CycleRemoteSyncInterval(target.Name);
    }

    internal void CommitRemoveRemoteToken(SkillSourceActionTarget? target)
    {
        if (target is null)
        {
            SetStatus("A remote skill server must be selected before removing a token.", ConfigStatusTone.Error);
            return;
        }

        RemoveRemoteToken(target.Name);
    }

    internal void CommitRemoveSource(SkillSourceActionTarget? target)
    {
        if (target is null)
        {
            SetStatus("A skill source must be selected before removing it.", ConfigStatusTone.Error);
            return;
        }

        RemoveSource(target.Kind, target.Name);
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
            case SkillSourcesScreen.AddRemoteToken:
                if (_editingAction == SkillSourceDetailAction.RotateToken)
                {
                    ShowDetail();
                    break;
                }

                // Probe-driven flow: the token field was reached from the URL probe, so
                // Back returns to the URL entry (there is no separate auth-choice screen).
                ShowTextScreen(SkillSourcesScreen.AddRemoteUrl, _pendingRemoteUrl ?? string.Empty);
                break;
            case SkillSourcesScreen.AddRemoteName:
                if (_pendingRemoteAuthMode == SkillSourceAuthMode.BearerToken)
                    ShowTextScreen(SkillSourcesScreen.AddRemoteToken, _pendingRemoteApiKey ?? string.Empty);
                else
                    ShowTextScreen(SkillSourcesScreen.AddRemoteUrl, _pendingRemoteUrl ?? string.Empty);
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
        // Cancel (but do not block-await) any in-flight off-loop probe: RunProbeAsync swallows the
        // resulting OperationCanceledException, so there is no unobserved exception to worry about.
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _countRefreshCts?.Cancel();
        _countRefreshCts?.Dispose();
        Screen.Dispose();
        SelectedRow.Dispose();
        Draft.Dispose();
        Version.Dispose();
        Status.Dispose();
        ActiveValidationDialog.Dispose();
        IsSaved.Dispose();
        base.Dispose();
    }

    // Exposes the in-flight off-loop reachability probe so tests can await it deterministically
    // (no Task.Delay) instead of racing the thread-pool continuation.
    internal Task? PendingProbe => _probeTask;

    // Title for the name-review step. Auto-advance drops the operator here without a confirming keypress, so
    // carry the probe's result line (e.g. "Skill feed reachable — 53 skills advertised.") onto this screen —
    // the way the init wizard shows "Found N skills" — instead of letting that confirmation vanish.
    internal string AddRemoteNameTitle =>
        string.IsNullOrWhiteSpace(_pendingRemoteProbeMessage)
            ? "Review remote skill server source."
            : _pendingRemoteProbeMessage!;

    // True only when the carried title is a reachable confirmation (so the page renders it success-green).
    // A save-anyway failure also lands here carrying its message, but must NOT read as success.
    internal bool AddRemoteNameTitleIsSuccess =>
        !string.IsNullOrWhiteSpace(_pendingRemoteProbeMessage) && _pendingRemoteProbeMessageIsSuccess;

    // Kicks off a reachability probe OFF the single-threaded TUI loop so a slow/unreachable feed
    // never freezes input. The synchronous setup (status + redraw) runs on the loop thread; the
    // continuation in RunProbeAsync resumes on the thread pool (no SyncContext here) and may ONLY
    // mutate Status/probe-result fields and RequestRedraw — never navigate.
    private void StartBackgroundProbe(
        string url,
        string? apiKey,
        int timeoutSeconds,
        string testingMessage,
        Action<SkillFeedReachabilityResult> onResult)
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = new CancellationTokenSource();
        SetStatus(testingMessage, ConfigStatusTone.Neutral);
        RequestRedraw();
        // Interactive probe: cap the wait so add/test feedback stays snappy even for a slow server. The
        // background Rescan all count refresh calls ExecuteProbeAsync directly with the feed's full timeout.
        _probeTask = ExecuteProbeAsync(
            url, apiKey, Math.Min(timeoutSeconds, InteractiveProbeTimeoutSeconds), _probeCts.Token, onResult);
    }

    // Shared off-loop probe execution for both the interactive add/test probe and the Rescan all count
    // refresh: run the probe, drop quietly if superseded/cancelled, otherwise hand the result to onResult and
    // redraw. onResult MUST be status/field-only (never navigation) — it may run on a thread-pool thread.
    private async Task ExecuteProbeAsync(
        string url,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken ct,
        Action<SkillFeedReachabilityResult> onResult)
    {
        SkillFeedReachabilityResult result;
        try
        {
            result = await _probe.ProbeAsync(url, apiKey, timeoutSeconds, ct);
        }
        catch (OperationCanceledException)
        {
            return; // superseded or abandoned probe — drop quietly
        }

        if (ct.IsCancellationRequested)
            return;

        onResult(result);
        RequestRedraw();
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

    internal SkillSourceCommitResult ValidateAddLocalPathDraft(string value)
        => TryNormalizeExternalDirectory(value.Trim(), out _, out var error)
            ? SkillSourceCommitResult.Ok()
            : SkillSourceCommitResult.Failed(error);

    internal void CommitAddLocalPathDraft(string value)
    {
        Draft.Value = value;
        ContinueAddLocalPath();
    }

    /// <summary>
    /// Applies a commit result coming from a page-driven field commit. Structural errors
    /// surface as a status line; a reachability-probe warning raises the "save anyway"
    /// override dialog, mirroring the prior validated-commit pipeline behavior.
    /// </summary>
    internal void ApplyCommitResult(SkillSourceCommitResult result)
    {
        if (result.Success)
            return;

        if (result.Tone == ConfigStatusTone.Warning)
        {
            CaptureValidationEditTarget();
            ActiveValidationDialog.Value = new NetclawValidationDialogModel(
                "Skill Server Validation Warning",
                "Netclaw could not complete skill server discovery using this configuration.",
                result.Message);
            Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
            RequestRedraw();
            return;
        }

        SetStatus(result.Message, result.Tone);
    }

    // ---------------------------------------------------------------------
    // Page-driven field commits.
    //
    // Each method below replaces a former validated-UI commit: read the staged
    // draft, run structural validation, optionally run the reachability probe,
    // then persist. Structural failures and probe warnings flow through
    // ApplyCommitResult (status line / override dialog). These are the entry
    // points the page calls on Enter / Space / Delete for each screen.
    // ---------------------------------------------------------------------

    internal void CommitAddLocalPath(string draft)
        => CommitStructural(draft, ValidateAddLocalPathDraft, CommitAddLocalPathDraft);

    internal void CommitAddLocalSymlinks(bool allowSymlinks)
    {
        var result = ValidateAddLocalSymlinksDraft(allowSymlinks);
        if (!result.Success)
        {
            ApplyCommitResult(result);
            return;
        }

        CommitAddLocalSymlinksDraft(allowSymlinks);
    }

    internal void CommitAddLocalName(string draft)
        => CommitStructural(draft, ValidateAddLocalNameDraft, CommitAddLocalNameDraft);

    internal void CommitAddRemoteUrl(string draft)
        => CommitStructural(draft, ValidateAddRemoteUrlDraft, CommitAddRemoteUrlDraft);

    internal void CommitAddRemoteToken(string draft)
    {
        var structural = ValidateAddRemoteTokenDraft(draft);
        if (!structural.Success)
        {
            ApplyCommitResult(structural);
            return;
        }

        ReplaceDraft(draft);
        // Reachability is no longer gated here (a blocking probe froze the loop). The add-remote
        // review step (ProbePendingRemoteThenReview) and the Test action validate reachability
        // off-loop; advance now.
        CommitAddRemoteTokenDraft(draft);
    }

    internal void CommitAddRemoteName(string draft)
        => CommitStructural(draft, ValidateAddRemoteNameDraft, CommitAddRemoteNameDraft);

    internal void CommitRenameSource(string draft)
        => CommitStructural(draft, ValidateRenameSourceDraft, CommitRenameSourceDraft);

    internal void CommitChangeLocation(string draft)
    {
        var structural = ValidateChangeLocationDraft(draft);
        if (!structural.Success)
        {
            ApplyCommitResult(structural);
            return;
        }

        ReplaceDraft(draft);

        // Persist now, validate reachability async: a blocking probe here froze the loop (deep-review
        // finding). For a remote source the persist path (SaveRemoteUrlChange) already kicks off the
        // off-loop warn-probe, so there is nothing to gate here — just commit.
        CommitChangeLocationDraft(draft);
    }

    internal void CommitToggleEnabledAction()
        => CommitSourceAction(ValidateSourceActionTarget, CommitToggleEnabled);

    internal void CommitToggleLocalSymlinksAction()
        => CommitSourceAction(ValidateLocalSourceActionTarget, CommitToggleLocalSymlinks);

    internal void CommitCycleRemoteSyncIntervalAction()
        => CommitSourceAction(ValidateRemoteSourceActionTarget, CommitCycleRemoteSyncInterval);

    internal void CommitRemoveRemoteTokenAction()
        => CommitSourceAction(ValidateRemoteSourceActionTarget, CommitRemoveRemoteToken);

    internal void CommitRemoveSourceAction()
        => CommitSourceAction(ValidateSourceActionTarget, CommitRemoveSource);

    private void CommitStructural(
        string draft,
        Func<string, SkillSourceCommitResult> validate,
        Action<string> persist)
    {
        var result = validate(draft);
        if (!result.Success)
        {
            ApplyCommitResult(result);
            return;
        }

        persist(draft);
    }

    private void CommitSourceAction(
        Func<SkillSourceActionTarget?, SkillSourceCommitResult> validate,
        Action<SkillSourceActionTarget?> persist)
    {
        var target = ReadCurrentSourceActionTarget();
        var result = validate(target);
        if (!result.Success)
        {
            ApplyCommitResult(result);
            return;
        }

        persist(target);
    }

    /// <summary>
    /// Persists the current draft without re-running the reachability probe. Invoked when
    /// the user chooses "Save anyway" from the probe-warning override dialog. Dispatches by
    /// the screen the dialog was raised over so the correct section writer runs.
    /// </summary>
    internal void SaveCurrentDraftAnyway()
    {
        ActiveValidationDialog.Value = null;
        switch (Screen.Value)
        {
            case SkillSourcesScreen.AddRemoteToken:
                CommitAddRemoteTokenDraft(Draft.Value);
                break;
            case SkillSourcesScreen.ChangeLocation:
                CommitChangeLocationDraft(Draft.Value);
                break;
            default:
                RequestRedraw();
                break;
        }
    }

    internal void DismissValidationDialog()
    {
        ActiveValidationDialog.Value = null;
        _validationEditScreen = null;
        _validationEditDraft = null;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        RequestRedraw();
    }

    internal void ReturnToValidationEdit()
    {
        var editScreen = _validationEditScreen;
        var editDraft = _validationEditDraft;
        ActiveValidationDialog.Value = null;
        _validationEditScreen = null;
        _validationEditDraft = null;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);

        switch (editScreen)
        {
            case SkillSourcesScreen.AddRemoteUrl:
            case SkillSourcesScreen.AddRemoteToken:
            case SkillSourcesScreen.ChangeLocation:
                ShowTextScreen(editScreen.Value, editDraft ?? string.Empty);
                break;
            default:
                RequestRedraw();
                break;
        }
    }

    private void CaptureValidationEditTarget()
    {
        _validationEditScreen = Screen.Value switch
        {
            SkillSourcesScreen.AddRemoteToken => SkillSourcesScreen.AddRemoteToken,
            SkillSourcesScreen.ChangeLocation => SkillSourcesScreen.ChangeLocation,
            _ => Screen.Value,
        };
        _validationEditDraft = _validationEditScreen switch
        {
            SkillSourcesScreen.AddRemoteUrl => _pendingRemoteUrl ?? Draft.Value,
            SkillSourcesScreen.AddRemoteToken => Draft.Value,
            SkillSourcesScreen.ChangeLocation => Draft.Value,
            _ => Draft.Value,
        };
    }

    private void ContinueAddLocalSymlinks()
    {
        _pendingLocalAllowSymlinks = SelectedRow.Value == 1;
        var suggestedName = SuggestNameFromPath(_pendingLocalPath ?? "team-skills");
        ShowTextScreen(SkillSourcesScreen.AddLocalName, MakeUniqueName(suggestedName));
    }

    internal bool ReadAddLocalSymlinksDraft()
        => SelectedRow.Value == 1;

    internal void ReplaceAddLocalSymlinksDraft(bool value)
    {
        if (Screen.Value != SkillSourcesScreen.AddLocalSymlinks)
            return;

        var row = value ? 1 : 0;
        if (SelectedRow.Value == row)
            return;

        SelectedRow.Value = row;
        MarkDirty();
    }

    internal SkillSourceCommitResult ValidateAddLocalSymlinksDraft(bool value)
        => _pendingLocalPath is null
            ? SkillSourceCommitResult.Failed("Local folder path is required before choosing symlink policy.")
            : SkillSourceCommitResult.Ok();

    internal void CommitAddLocalSymlinksDraft(bool value)
    {
        _pendingLocalAllowSymlinks = value;
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

        if (!TryLoadExternalConfig(out var external)) return;
        external.Sources.Add(new ExternalSkillSource
        {
            Name = name,
            Path = _pendingLocalPath,
            Enabled = true,
            AllowSymlinks = _pendingLocalAllowSymlinks,
        });

        if (!SaveExternalConfig(external))
            return;
        ClearPendingFlow();
        ReloadSources();
        _selectedKind = SkillSourceKind.LocalFolder;
        _selectedName = name;
        ShowDetail($"Added local skill folder '{name}'.");
    }

    internal SkillSourceCommitResult ValidateAddLocalNameDraft(string value)
    {
        if (_pendingLocalPath is null)
            return SkillSourceCommitResult.Failed("Local folder path is required before adding a source.");

        var name = NormalizeSourceName(value);
        return ValidateNewSourceName(name, null, out var error)
            ? SkillSourceCommitResult.Ok()
            : SkillSourceCommitResult.Failed(error);
    }

    internal void CommitAddLocalNameDraft(string value)
    {
        Draft.Value = value;
        SaveNewLocalSource();
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

        // Probe-driven disclosure: probe with no auth first. The bearer-token field is
        // revealed only when the server actually requires auth (401/403); open targets
        // go straight to the name/review step and never see a secret field.
        //
        // Do NOT reset _lastProbeFingerprint / _pendingRemoteProbeResult here: this method runs on
        // every Enter on the URL screen with the SAME URL, so the recomputed fingerprint matches the
        // first probe's. Clearing them would re-arm phase 1 and defeat "press Enter again to save
        // anyway" for an unreachable open server (the second Enter must act on the completed result).
        ProbePendingRemoteThenReview();
    }

    internal SkillSourceCommitResult ValidateAddRemoteUrlDraft(string value)
        => TryNormalizeFeedUrl(value.Trim(), out _, out var error)
            ? SkillSourceCommitResult.Ok()
            : SkillSourceCommitResult.Failed(error);

    internal void CommitAddRemoteUrlDraft(string value)
    {
        Draft.Value = value;
        ContinueAddRemoteUrl();
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

    internal SkillSourceCommitResult ValidateAddRemoteTokenDraft(string value)
    {
        if (_editingAction == SkillSourceDetailAction.RotateToken)
        {
            if (SelectedSource is not { Kind: SkillSourceKind.RemoteSkillServer })
                return SkillSourceCommitResult.Failed("A remote skill server must be selected before rotating a token.");
        }
        else if (_pendingRemoteUrl is null)
        {
            return SkillSourceCommitResult.Failed("Skill server URL is required before adding a token.");
        }

        var token = value.Trim();
        if (!TryValidateApiKeyDraft(token, out var error))
            return SkillSourceCommitResult.Failed(error);

        return string.IsNullOrWhiteSpace(token)
            ? SkillSourceCommitResult.Failed(_editingAction == SkillSourceDetailAction.RotateToken
                ? "New bearer token is required. Use Remove token to delete an existing token."
                : "Bearer token is required when authentication is set to bearer token.")
            : SkillSourceCommitResult.Ok();
    }

    internal void CommitAddRemoteTokenDraft(string value)
    {
        Draft.Value = value;
        if (_editingAction == SkillSourceDetailAction.RotateToken)
        {
            SaveRotatedRemoteToken();
            return;
        }

        _pendingRemoteApiKey = value.Trim();
        var suggestedName = SuggestNameFromUrl(_pendingRemoteUrl ?? "skill-server");
        ShowTextScreen(SkillSourcesScreen.AddRemoteName, MakeUniqueName(suggestedName));
    }

    // Two-phase add-remote review. The reachability probe runs OFF the loop (StartBackgroundProbe),
    // so this method must never navigate from a probe continuation. Instead it acts on a completed
    // probe result on the NEXT loop-thread invocation (the next Enter), where navigation is safe:
    //   Phase 1 (new fingerprint): kick off the probe, return. The background continuation only sets
    //           _pendingRemoteProbeResult + Status.
    //   Phase 2 (fingerprint matches, result present): ACT on the result here, on the loop thread —
    //           reveal the token field (RequiresAuth), advance to the name screen (success), or
    //           save-anyway to the name screen (a second Enter on a failure).
    // Editing the URL or entering a token changes the fingerprint, which re-arms phase 1.
    private void ProbePendingRemoteThenReview()
    {
        if (_pendingRemoteUrl is null)
        {
            SetStatus("Skill server URL is required before testing a source.", ConfigStatusTone.Error);
            return;
        }

        var apiKey = _pendingRemoteAuthMode == SkillSourceAuthMode.BearerToken ? _pendingRemoteApiKey : null;
        var fingerprint = $"{_pendingRemoteUrl}|{apiKey?.Length ?? 0}";

        if (_lastProbeFingerprint == fingerprint && _pendingRemoteProbeResult is { } done)
        {
            // Phase 2: a completed probe for this exact URL/token — navigate on the loop thread.
            _pendingRemoteProbeResult = null;

            if (done.Success)
            {
                ShowTextScreen(SkillSourcesScreen.AddRemoteName, MakeUniqueName(SuggestNameFromUrl(_pendingRemoteUrl)));
                return;
            }

            // Probe-driven disclosure: a 401/403 with no bearer token means the server requires
            // auth — reveal the token field rather than offering "save anyway". Entering a token
            // changes the fingerprint, which re-arms the probe.
            if (done.RequiresAuth && _pendingRemoteAuthMode != SkillSourceAuthMode.BearerToken)
            {
                _pendingRemoteAuthMode = SkillSourceAuthMode.BearerToken;
                ShowTextScreen(SkillSourcesScreen.AddRemoteToken, string.Empty);
                SetStatus($"{done.Message} Enter a bearer token to continue.", ConfigStatusTone.Warning);
                return;
            }

            // Save-anyway: a second Enter on a (non-auth) failure proceeds to the name screen.
            ShowTextScreen(SkillSourcesScreen.AddRemoteName, MakeUniqueName(SuggestNameFromUrl(_pendingRemoteUrl)));
            return;
        }

        if (_lastProbeFingerprint == fingerprint && _pendingRemoteProbeResult is null)
        {
            // Phase 1 probe still in flight — the user pressed Enter again before it returned.
            SetStatus("Still testing skill server…", ConfigStatusTone.Neutral);
            return;
        }

        // Phase 1: a new URL/token — kick off the off-loop probe. The continuation is status-only EXCEPT it
        // marshals the success advance onto the loop thread (navigation is never safe off-loop).
        _lastProbeFingerprint = fingerprint;
        _pendingRemoteProbeResult = null;
        StartBackgroundProbe(
            _pendingRemoteUrl,
            apiKey,
            _pendingRemoteTimeoutSeconds,
            "Testing skill server…",
            r =>
            {
                _pendingRemoteProbeResult = r;
                _pendingRemoteProbeMessage = r.Message;
                _pendingRemoteProbeMessageIsSuccess = r.Success;

                if (r.Success)
                {
                    // A reachable feed needs no decision, so advance straight to the name step like the init
                    // wizard rather than making the operator press Enter again. Navigation must run on the
                    // loop thread; marshal it there via InvokeAsync (this continuation runs off-loop). The
                    // marshaled AutoAdvanceAfterProbeSuccess re-checks that nothing changed in flight, and the
                    // ct skips the advance if a newer probe or a dispose has superseded this one. In tests the
                    // unbound InvokeAsync runs inline, so the advance lands within the awaited probe task.
                    SetStatus(r.Message, ConfigStatusTone.Success);
                    var ct = _probeCts?.Token ?? CancellationToken.None;
                    _ = InvokeAsync(() => AutoAdvanceAfterProbeSuccess(fingerprint), ct);
                    return;
                }

                // Failures still pause for a deliberate keypress: a 401 needs the operator to add a bearer
                // token, and an unreachable feed needs an explicit "save anyway" decision.
                SetStatus(
                    r.RequiresAuth && _pendingRemoteAuthMode != SkillSourceAuthMode.BearerToken
                        ? $"{r.Message} Press Enter to add a bearer token."
                        : $"{r.Message} Press Enter to save anyway.",
                    ConfigStatusTone.Warning);
            });
    }

    // Loop-thread continuation of a successful add-server probe: advance to the name step. Re-checks that the
    // operator did not edit the URL/token or leave the URL screen while the probe was in flight, so a stale
    // marshaled call can never hijack navigation. The phase-2 success branch above is the fallback for the
    // rare race where the operator presses Enter before this marshaled advance reaches the loop.
    private void AutoAdvanceAfterProbeSuccess(string fingerprint)
    {
        // A probe is launched from either the URL screen or (after a 401) the token screen; advance only if
        // still on one of those — if the operator backed out of the add flow, the marshaled call is stale.
        if (Screen.Value is not (SkillSourcesScreen.AddRemoteUrl or SkillSourcesScreen.AddRemoteToken)
            || _lastProbeFingerprint != fingerprint
            || _pendingRemoteProbeResult is not { Success: true }
            || _pendingRemoteUrl is null)
            return;

        _pendingRemoteProbeResult = null;
        ShowTextScreen(SkillSourcesScreen.AddRemoteName, MakeUniqueName(SuggestNameFromUrl(_pendingRemoteUrl)));
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

        if (!TryLoadSkillFeeds(out var feeds)) return;

        string? protectedApiKey = null;
        if (_pendingRemoteAuthMode == SkillSourceAuthMode.BearerToken
            && !string.IsNullOrWhiteSpace(_pendingRemoteApiKey)
            && !TryProtectApiKey(_pendingRemoteApiKey, out protectedApiKey))
            return;

        feeds.Feeds.Add(new SkillFeedConfigEntry
        {
            Name = name,
            Url = _pendingRemoteUrl,
            Enabled = true,
            TimeoutSeconds = _pendingRemoteTimeoutSeconds,
            ApiKey = protectedApiKey,
        });

        if (!SaveSkillFeedsConfig(feeds))
            return;
        ClearPendingFlow();
        ReloadSources();
        _selectedKind = SkillSourceKind.RemoteSkillServer;
        _selectedName = name;
        ShowDetail($"Added skill server '{name}'.");
    }

    internal SkillSourceCommitResult ValidateAddRemoteNameDraft(string value)
    {
        if (_pendingRemoteUrl is null)
            return SkillSourceCommitResult.Failed("Skill server URL is required before adding a source.");

        var name = NormalizeSourceName(value);
        return ValidateNewSourceName(name, null, out var error)
            ? SkillSourceCommitResult.Ok()
            : SkillSourceCommitResult.Failed(error);
    }

    internal void CommitAddRemoteNameDraft(string value)
    {
        Draft.Value = value;
        SaveNewRemoteSource();
    }

    private void ToggleEnabled(SkillSourceKind kind, string name)
    {
        if (kind == SkillSourceKind.LocalFolder)
        {
            if (!TryLoadExternalConfig(out var external)) return;
            var source = FindLocalSource(external, name);
            if (source is null)
            {
                SetStatus($"Local skill folder '{name}' no longer exists in config.", ConfigStatusTone.Error);
                ReloadSources();
                return;
            }

            source.Enabled = !source.Enabled;
            if (!SaveExternalConfig(external))
                return;
            ReloadSources();
            SetStatus($"Local skill folder '{name}' {(source.Enabled ? "enabled" : "disabled")}.", ConfigStatusTone.Success);
            return;
        }

        if (!TryLoadSkillFeeds(out var feeds)) return;
        var feed = FindRemoteSource(feeds, name);
        if (feed is null)
        {
            SetStatus($"Skill server '{name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        feed.Enabled = !feed.Enabled;
        if (!SaveSkillFeedsConfig(feeds))
            return;
        ReloadSources();
        SetStatus($"Skill server '{name}' {(feed.Enabled ? "enabled" : "disabled")}.", ConfigStatusTone.Success);
    }

    private void ToggleLocalSymlinks(string name)
    {
        if (!TryLoadExternalConfig(out var external)) return;
        var source = FindLocalSource(external, name);
        if (source is null)
        {
            SetStatus($"Local skill folder '{name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        source.AllowSymlinks = !source.AllowSymlinks;
        if (!SaveExternalConfig(external))
            return;
        ReloadSources();
        SetStatus($"Local skill folder '{name}' symlink policy saved.", ConfigStatusTone.Success);
    }

    private void CycleRemoteSyncInterval(string name)
    {
        if (!TryLoadSkillFeeds(out var feeds)) return;
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

        if (!SaveSkillFeedsConfig(feeds))
            return;
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

        if (!TryLoadSkillFeeds(out var feeds)) return;
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

        StartBackgroundProbe(
            feed.Url,
            apiKey,
            feed.TimeoutSeconds,
            $"Testing skill server '{source.Name}'…",
            r => SetStatus(r.Message, r.Success ? ConfigStatusTone.Success : ConfigStatusTone.Warning));
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
            if (!TryLoadExternalConfig(out var external)) return;
            var item = FindLocalSource(external, source.Name);
            if (item is null)
            {
                SetStatus($"Local skill folder '{source.Name}' no longer exists in config.", ConfigStatusTone.Error);
                ReloadSources();
                return;
            }

            item.Name = newName;
            if (!SaveExternalConfig(external))
                return;
        }
        else
        {
            if (!TryLoadSkillFeeds(out var feeds)) return;
            var item = FindRemoteSource(feeds, source.Name);
            if (item is null)
            {
                SetStatus($"Skill server '{source.Name}' no longer exists in config.", ConfigStatusTone.Error);
                ReloadSources();
                return;
            }

            item.Name = newName;
            if (!SaveSkillFeedsConfig(feeds))
                return;
        }

        _selectedName = newName;
        ReloadSources();
        ShowDetail($"Renamed source to '{newName}'.");
    }

    internal SkillSourceCommitResult ValidateRenameSourceDraft(string value)
    {
        if (SelectedSource is not { } source)
            return SkillSourceCommitResult.Failed("A skill source must be selected before renaming.");

        var newName = NormalizeSourceName(value);
        return ValidateNewSourceName(newName, source.Name, out var error)
            ? SkillSourceCommitResult.Ok()
            : SkillSourceCommitResult.Failed(error);
    }

    internal void CommitRenameSourceDraft(string value)
    {
        Draft.Value = value;
        SaveRename();
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

    internal SkillSourceCommitResult ValidateChangeLocationDraft(string value)
    {
        if (SelectedSource is not { } source)
            return SkillSourceCommitResult.Failed("A skill source must be selected before changing location.");

        if (source.Kind == SkillSourceKind.LocalFolder)
        {
            if (source.IsWellKnown)
                return SkillSourceCommitResult.Failed("Well-known source paths are managed automatically.");

            return TryNormalizeExternalDirectory(value.Trim(), out _, out var error)
                ? SkillSourceCommitResult.Ok()
                : SkillSourceCommitResult.Failed(error);
        }

        return TryNormalizeFeedUrl(value.Trim(), out _, out var urlError)
            ? SkillSourceCommitResult.Ok()
            : SkillSourceCommitResult.Failed(urlError);
    }

    internal void CommitChangeLocationDraft(string value)
    {
        Draft.Value = value;
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

        if (!TryLoadExternalConfig(out var external)) return;
        var item = FindLocalSource(external, source.Name);
        if (item is null)
        {
            SetStatus($"Local skill folder '{source.Name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        item.Path = fullPath;
        if (!SaveExternalConfig(external))
            return;
        ReloadSources();
        ShowDetail($"Local skill folder '{source.Name}' path saved.");
    }

    // Persist-now, validate-async: the URL change is saved immediately (a blocking probe froze the
    // loop), then an off-loop warn-probe surfaces a non-blocking warning if the new URL is unreachable.
    private void SaveRemoteUrlChange(SkillSourceDisplay source)
    {
        if (!TryNormalizeFeedUrl(Draft.Value.Trim(), out var url, out var error))
        {
            SetStatus(error, ConfigStatusTone.Error);
            return;
        }

        var normalizedUrl = url ?? throw new InvalidOperationException("Validated skill server URL was null.");

        if (!TryLoadSkillFeeds(out var feeds)) return;
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

        var timeoutSeconds = item.TimeoutSeconds;
        item.Url = normalizedUrl;
        if (!SaveSkillFeedsConfig(feeds))
            return;
        ReloadSources();
        ShowDetail($"Skill server '{source.Name}' URL saved.");

        StartBackgroundProbe(
            normalizedUrl,
            apiKey,
            timeoutSeconds,
            "Verifying skill server…",
            r => SetStatus(
                r.Success
                    ? $"Skill server '{source.Name}' URL saved (reachable)."
                    : $"Saved, but the skill server is unreachable: {r.Message}",
                r.Success ? ConfigStatusTone.Success : ConfigStatusTone.Warning));
    }

    // Persist-now, validate-async: the rotated token is saved immediately, then an off-loop
    // warn-probe surfaces a non-blocking warning if the feed is unreachable with the new token.
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

        if (!TryLoadSkillFeeds(out var feeds)) return;
        var feed = FindRemoteSource(feeds, source.Name);
        if (feed is null)
        {
            SetStatus($"Skill server '{source.Name}' no longer exists in config.", ConfigStatusTone.Error);
            ReloadSources();
            return;
        }

        var feedUrl = feed.Url;
        var timeoutSeconds = feed.TimeoutSeconds;
        if (!TryProtectApiKey(token, out var protectedToken))
            return;
        feed.ApiKey = protectedToken;
        if (!SaveSkillFeedsConfig(feeds))
            return;
        _editingAction = null;
        ReloadSources();
        ShowDetail($"Skill server '{source.Name}' token rotated.");

        StartBackgroundProbe(
            feedUrl,
            token,
            timeoutSeconds,
            "Verifying skill server…",
            r => SetStatus(
                r.Success
                    ? $"Skill server '{source.Name}' token rotated (reachable)."
                    : $"Saved, but the skill server is unreachable: {r.Message}",
                r.Success ? ConfigStatusTone.Success : ConfigStatusTone.Warning));
    }

    private void RemoveRemoteToken(string name)
    {
        if (!TryLoadSkillFeeds(out var feeds)) return;
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
        if (!SaveSkillFeedsConfig(feeds))
            return;
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

        RemoveSource(kind, name);
    }

    private void RemoveSource(SkillSourceKind kind, string name)
    {
        if (kind == SkillSourceKind.LocalFolder)
        {
            if (!TryLoadExternalConfig(out var external)) return;
            external.Sources.RemoveAll(s => _nameComparer.Equals(s.Name, name));
            if (!SaveExternalConfig(external))
                return;
        }
        else
        {
            if (!TryLoadSkillFeeds(out var feeds)) return;
            feeds.Feeds.RemoveAll(f => _nameComparer.Equals(f.Name, name));
            if (!SaveSkillFeedsConfig(feeds))
                return;
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
        RefreshRemoteSkillCounts($"Rescanned {localCount} local folder(s).");
    }

    // Exposes the in-flight Rescan-all count refresh so tests can await it deterministically (no Task.Delay).
    // Always a Task once Rescan all has run (Task.CompletedTask when nothing was probed), never null after.
    internal Task? PendingCountRefresh => _countRefreshTask;

    // Refresh cached skill counts for every ENABLED remote server OFF the loop (triggered by Rescan all). Each
    // probe runs on the thread pool (ExecuteProbeAsync — the same off-loop discipline as the single add/test
    // probe) and writes its count into the thread-safe cache. Owns the Rescan all status: the in-progress line
    // is posted BEFORE the summary task is launched, so a synchronously-completing probe's final status (via
    // SummarizeRemoteCountsAsync) lands last instead of being overwritten by a "Refreshing…" line.
    private void RefreshRemoteSkillCounts(string localSummary)
    {
        _countRefreshCts?.Cancel();
        _countRefreshCts?.Dispose();
        _countRefreshCts = new CancellationTokenSource();
        var ct = _countRefreshCts.Token;

        var probes = new List<Task<bool>>();
        var credentialFailures = 0;
        if (TryLoadSkillFeeds(out var feeds))
        {
            // Disabled feeds contribute nothing to the running daemon, so don't probe or badge them.
            foreach (var source in _sources.Where(static s => s.Kind == SkillSourceKind.RemoteSkillServer && s.Enabled))
            {
                var feed = FindRemoteSource(feeds, source.Name);
                if (feed is null)
                    continue;

                // A stored token that will not decrypt must NOT silently degrade to an anonymous probe — that
                // would hide the credential failure and send an unauthenticated request to a server the operator
                // configured a token for. Skip the feed and surface the failure through the Rescan all status.
                if (!TryGetFeedApiKeyPlaintext(feed, out var apiKey, out _))
                {
                    credentialFailures++;
                    continue;
                }

                probes.Add(ProbeAndCacheSkillCountAsync(source.Name, feed.Url, apiKey, feed.TimeoutSeconds, ct));
            }
        }

        var summary = localSummary;
        if (credentialFailures > 0)
            summary += $" {credentialFailures} server(s) skipped — stored token could not be decrypted.";

        if (probes.Count > 0)
            SetStatus(
                $"{summary} Refreshing {probes.Count} skill server count(s)…",
                credentialFailures > 0 ? ConfigStatusTone.Warning : ConfigStatusTone.Neutral);
        else
            SetStatus(summary, credentialFailures > 0 ? ConfigStatusTone.Warning : ConfigStatusTone.Success);

        _countRefreshTask = probes.Count > 0 ? SummarizeRemoteCountsAsync(probes, ct) : Task.CompletedTask;
    }

    // Probe one server with the feed's full configured timeout and cache its advertised count. Returns true
    // only if the server actually reported a count, so RescanAll can report how many responded.
    private async Task<bool> ProbeAndCacheSkillCountAsync(string name, string url, string? apiKey, int timeoutSeconds, CancellationToken ct)
    {
        var reported = false;
        await ExecuteProbeAsync(url, apiKey, timeoutSeconds, ct, result =>
        {
            if (result.SkillCount is not { } count)
                return;

            _remoteSkillCounts[name] = count;
            reported = true;
        });
        return reported;
    }

    // Once every count probe has settled, report how many servers actually responded so an all-failed refresh
    // does not read as success. Off-loop; the ct guard drops the update if the refresh was superseded/disposed.
    private async Task SummarizeRemoteCountsAsync(List<Task<bool>> probes, CancellationToken ct)
    {
        var outcomes = await Task.WhenAll(probes);
        if (ct.IsCancellationRequested)
            return;

        var reported = outcomes.Count(static r => r);
        var total = outcomes.Length;
        if (reported == total)
            SetStatus($"Updated skill counts for {total} skill server(s).", ConfigStatusTone.Success);
        else
            SetStatus(
                $"Updated skill counts for {reported} of {total} skill server(s); {total - reported} did not respond.",
                ConfigStatusTone.Warning);
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
                FormatSourceDetail(source),
                source.StatusTone));
        }

        rows.Add(new SkillSourcesInventoryRow(SkillSourcesInventoryAction.AddLocalFolder, null, null, "+ Add local folder", "Scan a directory on this machine.", ConfigStatusTone.Neutral));
        rows.Add(new SkillSourcesInventoryRow(SkillSourcesInventoryAction.AddSkillServer, null, null, "+ Add skill server", "Connect to a remote skill feed.", ConfigStatusTone.Neutral));
        rows.Add(new SkillSourcesInventoryRow(SkillSourcesInventoryAction.RescanAll, null, null, "Rescan all", "Refresh local status and remote skill counts.", ConfigStatusTone.Neutral));
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
            new(SkillSourceDetailAction.Authentication,
                $"Authentication          {(source.HasApiKey ? source.ApiKeyIsPlaintext ? "bearer token stored as PLAINTEXT" : "bearer token configured" : "none")}",
                source.ApiKeyIsPlaintext
                    ? "Token is stored unencrypted in config — use Rotate token to re-enter and encrypt it."
                    : "Use Rotate token or Remove token for credentials.",
                source.ApiKeyIsPlaintext ? ConfigStatusTone.Warning : ConfigStatusTone.Neutral),
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
        try
        {
            var root = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
            var external = ConfigFileHelper.LoadSection<ExternalSkillsConfig>(root, "ExternalSkills");
            var feeds = LoadSkillFeedsSection(root);
            _sources = BuildSources(external, feeds).ToList();
            // A reload means a source's identity or config may have changed (rename, URL change, remove,
            // enable/disable) — any of which invalidates a cached remote skill count. Drop the cache so a
            // stale or misattributed count cannot survive the change; the next Rescan all repopulates it.
            _remoteSkillCounts.Clear();
            Version.Value++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A malformed / unreadable netclaw.json must not crash the page or its constructor (this
            // runs from both). Keep the prior _sources snapshot (empty on first load) and surface the
            // error so the operator can repair the file instead of facing a dead page.
            SetStatus($"Could not read skill sources config: {ex.Message}", ConfigStatusTone.Error);
        }
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
                ConfigStatusTone.Neutral,
                ApiKeyIsPlaintext: !string.IsNullOrWhiteSpace(feed.ApiKey) && !ISecretsProtector.IsEncrypted(feed.ApiKey));
        }
    }

    private int RowCountForCurrentScreen()
        => Screen.Value switch
        {
            SkillSourcesScreen.Inventory => InventoryRows.Count,
            SkillSourcesScreen.SourceDetail => DetailRows.Count,
            SkillSourcesScreen.AddLocalSymlinks => 2,
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
        SelectedRow.Value = 0;
        Draft.Value = seed;
        Screen.Value = screen;
        ClearStatus();
        RequestRedraw();
    }

    private void ShowChoiceScreen(SkillSourcesScreen screen, int row)
    {
        SelectedRow.Value = row;
        Draft.Value = string.Empty;
        Screen.Value = screen;
        ClearStatus();
        RequestRedraw();
    }

    private void MarkDirty()
    {
        IsSaved.Value = false;
        // Re-arm the add-remote review probe: a config edit invalidates any completed probe result.
        _lastProbeFingerprint = null;
        _pendingRemoteProbeResult = null;
        ActiveValidationDialog.Value = null;
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
        _lastProbeFingerprint = null;
        _pendingRemoteProbeResult = null;
        _editingAction = null;
        ActiveValidationDialog.Value = null;
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
        => ConfigFileHelper.LoadSection<ExternalSkillsConfig>(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath), "ExternalSkills");

    // Guarded pre-save reads. Every mutation handler reads the current config (LoadJsonDict ->
    // deserialize -> typed LoadSection) BEFORE handing the result to TryEditConfig for the write.
    // That read sits outside TryEditConfig's guard, so a malformed / partially-written netclaw.json
    // (JsonException) or a disk/permission error would escape into the Termina event loop on every
    // add/toggle/rename/remove. Route those reads through these so a read failure surfaces via Status
    // and the handler early-returns, exactly as the write path does.
    private bool TryLoadExternalConfig(out ExternalSkillsConfig external)
    {
        try
        {
            external = LoadExternalConfig();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            external = new ExternalSkillsConfig();
            SetStatus($"Could not read skill sources config: {ex.Message}", ConfigStatusTone.Error);
            return false;
        }
    }

    private bool TryLoadSkillFeeds(out SkillFeedsConfigDocument feeds)
    {
        try
        {
            feeds = LoadSkillFeedsSection(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            feeds = new SkillFeedsConfigDocument();
            SetStatus($"Could not read skill feeds config: {ex.Message}", ConfigStatusTone.Error);
            return false;
        }
    }

    private bool SaveExternalConfig(ExternalSkillsConfig external)
        => TryEditConfig(root =>
        {
            if (external.Sources.Count == 0)
                root.Remove("ExternalSkills");
            else
                root["ExternalSkills"] = BuildExternalSkillsSection(external);
        });

    private bool SaveSkillFeedsConfig(SkillFeedsConfigDocument feeds)
        => TryEditConfig(root =>
        {
            if (feeds.Feeds.Count == 0)
                root.Remove("SkillFeeds");
            else
                root["SkillFeeds"] = BuildSkillFeedsSection(feeds);
        });

    // Reads, mutates, and writes the config root as one guarded unit, surfacing a disk-write IO
    // failure (disk full, permission denied, path too long — PathTooLongException derives from
    // IOException) OR a malformed existing netclaw.json (LoadJsonDict deserializes it, so a
    // hand-edited file throws JsonException on the read) as an error status instead of letting it
    // propagate into the Termina event loop and crash the page. The read previously sat outside the
    // guard. Returns false on failure so the caller skips its success/navigation path.
    private bool TryEditConfig(Action<Dictionary<string, object>> mutate)
    {
        try
        {
            var root = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
            root["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
            mutate(root);
            ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"Could not save skill sources config: {ex.Message}", ConfigStatusTone.Error);
            return false;
        }
    }

    private ExternalSkillSource? FindLocalSource(ExternalSkillsConfig external, string name)
        => external.Sources.FirstOrDefault(source => _nameComparer.Equals(source.Name, name));

    private SkillFeedConfigEntry? FindRemoteSource(SkillFeedsConfigDocument feeds, string name)
        => feeds.Feeds.FirstOrDefault(feed => _nameComparer.Equals(feed.Name, name));

    private static bool IsTextEntryScreen(SkillSourcesScreen screen)
        // AddLocalPath is intentionally excluded: it is an interactive directory picker, not a
        // text field, so keystrokes/paste route to the picker rather than the draft.
        => screen is SkillSourcesScreen.AddLocalName
            or SkillSourcesScreen.AddRemoteUrl
            or SkillSourcesScreen.AddRemoteToken
            or SkillSourcesScreen.AddRemoteName
            or SkillSourcesScreen.RenameSource
            or SkillSourcesScreen.ChangeLocation;

    private static string FormatSourceLabel(SkillSourceDisplay source)
    {
        var enabled = source.Enabled ? "x" : " ";
        return $"[{enabled}] {FormatDisplayName(source.Name)}";
    }

    private string FormatSourceDetail(SkillSourceDisplay source)
    {
        if (source.Kind == SkillSourceKind.LocalFolder)
            return $"{TruncateMiddle(source.Location, 58)}  |  {source.StatusText}";

        var auth = source.HasApiKey ? "Token configured" : "No auth";
        var detail = $"{TruncateMiddle(HostOrLocation(source.Location), 42)}  |  {auth}";
        // "advertised" = the count the server publishes; the daemon may load fewer after scan/hash filtering.
        if (_remoteSkillCounts.TryGetValue(source.Name, out var count))
            detail += $"  |  {count} advertised";
        return detail;
    }

    private static string FormatDisplayName(string value)
    {
        var parts = value
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static part => !IsTopLevelDomainToken(part))
            .Select(static part => char.ToUpperInvariant(part[0]) + part[1..])
            .ToArray();

        return parts.Length == 0 ? value : string.Join(' ', parts);
    }

    private static bool IsTopLevelDomainToken(string value)
        => value is "com" or "net" or "org" or "io" or "dev";

    private static string HostOrLocation(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return value;
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

    // Encrypt an API key, surfacing a DataProtection key-ring failure (unavailable/rotated keys throw
    // CryptographicException; a missing/locked keys directory throws IOException) as an error status
    // instead of letting it escape into the Termina event loop. The .Protect() call ran before
    // TryEditConfig, so it was outside the write guard.
    private bool TryProtectApiKey(string apiKey, out string? protectedApiKey)
    {
        try
        {
            protectedApiKey = ProtectApiKeyForConfig(_paths, apiKey);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            protectedApiKey = null;
            SetStatus($"Could not encrypt the API key: {ex.Message}", ConfigStatusTone.Error);
            return false;
        }
    }

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

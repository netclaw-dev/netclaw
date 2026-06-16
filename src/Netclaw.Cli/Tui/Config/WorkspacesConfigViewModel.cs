// -----------------------------------------------------------------------
// <copyright file="WorkspacesConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using R3;
using Termina.Layout;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

internal sealed class WorkspacesConfigViewModel : ReactiveViewModel
{
    private readonly NetclawPaths _paths;

    public WorkspacesConfigViewModel(NetclawPaths paths, IFileSystemProvider? fileSystemProvider = null)
    {
        _paths = paths;
        FileSystemProvider = fileSystemProvider ?? new DefaultFileSystemProvider();
        // Degrade to no current directory on a malformed/unreadable netclaw.json rather than throwing
        // from the constructor (which would make the Workspaces page permanently inaccessible).
        string? loadError = null;
        string currentDirectory;
        try
        {
            currentDirectory = LoadCurrentDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            currentDirectory = string.Empty;
            loadError = $"Could not read netclaw.json: {ex.Message}";
        }
        CurrentDirectory = new ReactiveProperty<string>(currentDirectory);
        DirectoryDraft = new ReactiveProperty<string>(string.Empty);
        Status = new ReactiveProperty<ConfigStatusMessage>(loadError is null
            ? new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral)
            : new ConfigStatusMessage(loadError, ConfigStatusTone.Error));
        IsSaved = new ReactiveProperty<bool>(false);
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public IFileSystemProvider FileSystemProvider { get; }

    public ReactiveProperty<string> CurrentDirectory { get; }
    public ReactiveProperty<string> DirectoryDraft { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<bool> IsSaved { get; }

    /// <summary>
    /// Directory the picker opens at. Prefers the current workspaces directory when it exists
    /// (you are most likely re-pointing near it); otherwise the launch working directory. The
    /// picker can navigate up to the filesystem root and back down, so this is only an anchor.
    /// </summary>
    public string BrowseStartPath
    {
        get
        {
            var current = CurrentDirectory.Value;
            if (!string.IsNullOrWhiteSpace(current))
            {
                if (FileSystemProvider.DirectoryExists(current))
                    return current;

                // The configured dir does not exist yet (e.g. never created, or removed): open at
                // its parent so you stay in the right neighborhood rather than the process working
                // directory (which can be the binary's location).
                var parent = FileSystemProvider.GetParentDirectory(current);
                if (!string.IsNullOrWhiteSpace(parent) && FileSystemProvider.DirectoryExists(parent))
                    return parent;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    /// <summary>
    /// Creates <paramref name="name"/> under <paramref name="parentPath"/> and selects it. The
    /// inline "new folder" affordance the picker lacks; <see cref="Save"/> performs the actual
    /// directory creation and persistence.
    /// </summary>
    public void CreateAndSelectFolder(string parentPath, string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Status.Value = new ConfigStatusMessage("Enter a valid folder name (no path separators).", ConfigStatusTone.Error);
            RequestRedraw();
            return;
        }

        ApplyPickedDirectory(Path.Combine(parentPath, trimmed));
    }

    /// <summary>
    /// Applies a directory chosen in the picker: stages it as the draft and saves immediately
    /// (picking an existing directory is itself the confirmation). The picker stays open with the
    /// new value reflected as Current.
    /// </summary>
    public void ApplyPickedDirectory(string path)
    {
        DirectoryDraft.Value = path;
        IsSaved.Value = false;
        Save();
    }

    public string CandidateDirectory => string.IsNullOrWhiteSpace(DirectoryDraft.Value)
        ? CurrentDirectory.Value
        : DirectoryDraft.Value;

    public void AppendText(string text)
    {
        DirectoryDraft.Value += text;
        IsSaved.Value = false;
        ClearStatus();
        RequestRedraw();
    }

    public void Backspace()
    {
        if (DirectoryDraft.Value.Length == 0)
            return;

        DirectoryDraft.Value = DirectoryDraft.Value[..^1];
        IsSaved.Value = false;
        ClearStatus();
        RequestRedraw();
    }

    public bool Save()
    {
        if (!TryNormalizeLocalDirectory(CandidateDirectory, out var fullPath, out var error))
        {
            Status.Value = new ConfigStatusMessage(error, ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        try
        {
            if (File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                Status.Value = new ConfigStatusMessage("Workspaces Directory must be a directory, not a file.", ConfigStatusTone.Error);
                RequestRedraw();
                return false;
            }

            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Status.Value = new ConfigStatusMessage($"Workspaces Directory could not be created: {ex.Message}", ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        try
        {
            // Read + modify + write as one guarded unit: LoadJsonDict deserializes netclaw.json, so a
            // malformed (hand-edited) config throws JsonException on the read — which sat outside the
            // guard and propagated into the Termina event loop.
            var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
            config["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
            ConfigFileHelper.SetPathValue(config, "Workspaces.Directory", fullPath);
            ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Status.Value = new ConfigStatusMessage($"Workspaces Directory could not be saved: {ex.Message}", ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        CurrentDirectory.Value = fullPath;
        DirectoryDraft.Value = string.Empty;
        IsSaved.Value = true;
        Status.Value = new ConfigStatusMessage("Workspaces Directory saved.", ConfigStatusTone.Success);
        RequestRedraw();
        return true;
    }

    public void GoBack()
    {
        RouteRequested?.Invoke("/config");
        Navigate?.Invoke("/config");
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    public override void Dispose()
    {
        CurrentDirectory.Dispose();
        DirectoryDraft.Dispose();
        Status.Dispose();
        IsSaved.Dispose();
        base.Dispose();
    }

    private string LoadCurrentDirectory()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        return ConfigFileHelper.TryGetPathValue(config, "Workspaces.Directory", out var value)
            ? new NetclawPaths(_paths.BasePath, value?.ToString()).WorkspacesDirectory
            : _paths.WorkspacesDirectory;
    }

    private void ClearStatus()
    {
        if (!string.IsNullOrWhiteSpace(Status.Value.Text))
            Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
    }

    private static bool TryNormalizeLocalDirectory(string value, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Workspaces Directory is required.";
            return false;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            error = "Workspaces Directory must be a local filesystem path, not a URL.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(PathExpansion.ExpandHome(trimmed) ?? trimmed);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Workspaces Directory is not a valid local path: {ex.Message}";
            return false;
        }
    }
}

// -----------------------------------------------------------------------
// <copyright file="WorkspacesConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

internal sealed class WorkspacesConfigViewModel : ReactiveViewModel
{
    private readonly NetclawPaths _paths;

    public WorkspacesConfigViewModel(NetclawPaths paths)
    {
        _paths = paths;
        CurrentDirectory = new ReactiveProperty<string>(LoadCurrentDirectory());
        DirectoryDraft = new ReactiveProperty<string>(string.Empty);
        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        IsSaved = new ReactiveProperty<bool>(false);
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<string> CurrentDirectory { get; }
    public ReactiveProperty<string> DirectoryDraft { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<bool> IsSaved { get; }

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

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        config["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        ConfigFileHelper.SetPathValue(config, "Workspaces.Directory", fullPath);
        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);

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

// -----------------------------------------------------------------------
// <copyright file="WorkspacesConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class WorkspacesConfigViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public WorkspacesConfigViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Workspaces_dashboard_entry_routes_to_real_editor()
    {
        using var vm = new Netclaw.Cli.Tui.ConfigDashboardViewModel(new Netclaw.Cli.Tui.ConfigDashboardNavigationState());
        string? route = null;
        vm.RouteRequested = r => route = r;

        vm.Activate(vm.Items.Single(static item => item.Label == "Workspaces Directory"));

        Assert.Equal("/workspaces", route);
    }

    [Fact]
    public void Save_persists_workspaces_directory_and_preserves_identity_files()
    {
        Directory.CreateDirectory(_paths.IdentityDirectory);
        File.WriteAllText(_paths.SoulPath, "original soul");
        File.WriteAllText(_paths.ToolingPath, "original tooling");
        var customWorkspaces = Path.Combine(_dir.Path, "custom-workspaces");
        using var vm = new WorkspacesConfigViewModel(_paths);

        vm.AppendText(customWorkspaces);

        Assert.True(vm.Save());

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Workspaces.Directory", out var value));
        Assert.Equal(customWorkspaces, value);
        Assert.True(Directory.Exists(customWorkspaces));
        Assert.Equal("original soul", File.ReadAllText(_paths.SoulPath));
        Assert.Equal("original tooling", File.ReadAllText(_paths.ToolingPath));
    }

    [Fact]
    public void Constructor_with_malformed_config_does_not_throw_and_surfaces_error()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ not valid json ");

        // Must not throw from the constructor (which would make the page permanently inaccessible).
        using var vm = new WorkspacesConfigViewModel(_paths);

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
    }

    [Fact]
    public void Save_rejects_url_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new WorkspacesConfigViewModel(_paths);

        vm.AppendText("https://example.com/workspaces");

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("local filesystem path", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_surfaces_malformed_config_read_failure_without_crashing()
    {
        var target = Path.Combine(_dir.Path, "workspaces");
        Directory.CreateDirectory(target);
        using var vm = new WorkspacesConfigViewModel(_paths);
        vm.AppendText(target);

        // Corrupt netclaw.json so the save-time LoadJsonDict read (which sat between the two
        // try/catch blocks, outside the guard) throws JsonException rather than an IOException.
        File.WriteAllText(_paths.NetclawConfigPath, "{ this is not valid json ");

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
    }

    [Fact]
    public void Save_rejects_existing_file_before_persistence()
    {
        var filePath = Path.Combine(_dir.Path, "not-a-directory");
        File.WriteAllText(filePath, "file");
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new WorkspacesConfigViewModel(_paths);

        vm.AppendText(filePath);

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Saved_directory_is_consumed_by_paths_and_prompt_workspace_context()
    {
        var customWorkspaces = Path.Combine(_dir.Path, "workspace-root");
        var projectDir = Path.Combine(customWorkspaces, "project-a");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "AGENTS.md"), "project-specific instructions");
        using var vm = new WorkspacesConfigViewModel(_paths);
        vm.AppendText(customWorkspaces);

        Assert.True(vm.Save());
        var runtimePaths = new NetclawPaths(_paths.BasePath, ReadConfiguredWorkspacesDirectory());
        var promptProvider = new FileSystemPromptProvider(runtimePaths);

        Assert.Equal(customWorkspaces, runtimePaths.WorkspacesDirectory);
        Assert.Contains("project-specific instructions", promptProvider.GetSystemPrompt(TrustAudience.Team, projectDir));
    }

    [Fact]
    public void ApplyPickedDirectory_persists_the_chosen_directory()
    {
        // A directory chosen in the picker is itself the confirmation: it stages + saves at once.
        var picked = Path.Combine(_dir.Path, "picked-workspaces");
        Directory.CreateDirectory(picked);
        using var vm = new WorkspacesConfigViewModel(_paths);

        vm.ApplyPickedDirectory(picked);

        Assert.True(vm.IsSaved.Value);
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Workspaces.Directory", out var value));
        Assert.Equal(picked, value);
    }

    [Fact]
    public void CreateAndSelectFolder_creates_persists_and_selects_a_new_subdirectory()
    {
        // The inline "new folder" affordance: create a subdir under where the picker is, then
        // select it (the directory must exist + be persisted afterward).
        var parent = Path.Combine(_dir.Path, "parent");
        Directory.CreateDirectory(parent);
        using var vm = new WorkspacesConfigViewModel(_paths);

        vm.CreateAndSelectFolder(parent, "fresh-workspace");

        var created = Path.Combine(parent, "fresh-workspace");
        Assert.True(Directory.Exists(created));
        Assert.True(vm.IsSaved.Value);
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Workspaces.Directory", out var value));
        Assert.Equal(created, value);
    }

    [Fact]
    public void CreateAndSelectFolder_rejects_an_invalid_name_without_persisting()
    {
        var parent = Path.Combine(_dir.Path, "parent");
        Directory.CreateDirectory(parent);
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new WorkspacesConfigViewModel(_paths);

        vm.CreateAndSelectFolder(parent, "bad/name");

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void BrowseStartPath_prefers_the_existing_current_directory()
    {
        var fileSystem = new StubFileSystemProvider(existingDirectories: [_paths.WorkspacesDirectory]);
        using var vm = new WorkspacesConfigViewModel(_paths, fileSystem);

        Assert.Equal(_paths.WorkspacesDirectory, vm.BrowseStartPath);
    }

    [Fact]
    public void BrowseStartPath_falls_back_to_the_parent_when_current_is_missing_but_parent_exists()
    {
        var parent = Path.GetDirectoryName(_paths.WorkspacesDirectory)!;
        var fileSystem = new StubFileSystemProvider(existingDirectories: [parent]);
        using var vm = new WorkspacesConfigViewModel(_paths, fileSystem);

        Assert.Equal(parent, vm.BrowseStartPath);
    }

    [Fact]
    public void BrowseStartPath_falls_back_to_home_when_neither_current_nor_parent_exist()
    {
        var fileSystem = new StubFileSystemProvider(existingDirectories: []);
        using var vm = new WorkspacesConfigViewModel(_paths, fileSystem);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), vm.BrowseStartPath);
    }

    private string ReadConfiguredWorkspacesDirectory()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Workspaces.Directory", out var value));
        return Assert.IsType<string>(value);
    }
}

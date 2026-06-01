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

    private string ReadConfiguredWorkspacesDirectory()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Workspaces.Directory", out var value));
        return Assert.IsType<string>(value);
    }
}

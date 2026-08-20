// -----------------------------------------------------------------------
// <copyright file="WorkspaceToolRelativePathTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Actors.Tools;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class WorkspaceToolRelativePathTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();
    private readonly string _projectDirectory;
    private readonly string _sessionDirectory;
    private readonly ToolConfig _config = new();
    private readonly ToolPathPolicy _pathPolicy = new([]);

    public WorkspaceToolRelativePathTests()
    {
        _projectDirectory = Path.Join(_temp.Path, "project");
        _sessionDirectory = Path.Join(_temp.Path, "sessions", "current");
        Directory.CreateDirectory(_projectDirectory);
        Directory.CreateDirectory(_sessionDirectory);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task File_read_resolves_relative_path_from_project()
    {
        var path = Path.Join(_projectDirectory, "README.md");
        await File.WriteAllTextAsync(path, "project read", TestContext.Current.CancellationToken);
        var context = CreateContext();
        var tool = new FileReadTool(_config, new NetclawPaths(), _pathPolicy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", "README.md"),
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal("project read", result);
        AssertSuccessfulActivity(context, path, ToolFileActivityKind.Read);
    }

    [Fact]
    public async Task File_list_resolves_relative_path_without_recording_file_activity()
    {
        var directory = Path.Join(_projectDirectory, "src");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Join(directory, "App.cs"),
            "class App {}",
            TestContext.Current.CancellationToken);
        var context = CreateContext();
        var tool = new FileListTool(_config, new NetclawPaths(), _pathPolicy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", "src"),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("App.cs", result, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.Success, context.Receipt?.Category);
        Assert.Empty(context.Receipt?.FileActivity ?? []);
    }

    [Fact]
    public async Task File_edit_resolves_relative_path_and_reports_change()
    {
        var path = Path.Join(_projectDirectory, "settings.json");
        await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);
        var context = CreateContext();
        var tool = new FileEditTool(_config, new NetclawPaths(), _pathPolicy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create(
                "Path", "settings.json",
                "OldString", "old",
                "NewString", "new"),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("Successfully edited", result, StringComparison.Ordinal);
        Assert.Equal("new", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        AssertSuccessfulActivity(context, path, ToolFileActivityKind.Changed);
    }

    [Fact]
    public async Task Attach_file_resolves_relative_source_and_reports_source_read()
    {
        var path = Path.Join(_projectDirectory, "report.txt");
        await File.WriteAllTextAsync(path, "report", TestContext.Current.CancellationToken);
        var context = CreateContext();
        var tool = new AttachFileTool(_config, new NetclawPaths(), _pathPolicy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", "report.txt"),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("File attached", result, StringComparison.Ordinal);
        AssertSuccessfulActivity(context, path, ToolFileActivityKind.Read);
    }

    [Fact]
    public async Task Relative_path_still_enters_protected_path_policy()
    {
        var path = Path.Join(_projectDirectory, "protected.json");
        await File.WriteAllTextAsync(path, "secret", TestContext.Current.CancellationToken);
        var context = CreateContext();
        var tool = new FileReadTool(_config, new NetclawPaths(), new ToolPathPolicy([path]));

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", "protected.json"),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("Access denied", result, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.AccessDenied, context.Receipt?.Category);
        Assert.Empty(context.Receipt?.FileActivity ?? []);
    }

    [Fact]
    public async Task Control_bearing_relative_path_fails_before_file_access()
    {
        var context = CreateContext();
        var tool = new FileReadTool(_config, new NetclawPaths(), _pathPolicy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", "bad\nname.txt"),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("Invalid path", result, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, context.Receipt?.Category);
        Assert.Empty(context.Receipt?.FileActivity ?? []);
    }

    [Fact]
    public async Task Relative_path_without_owned_base_returns_recoverable_correction()
    {
        var context = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal
        });
        var tool = new FileReadTool(_config, new NetclawPaths(), _pathPolicy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", "README.md"),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("invalid_context", result, StringComparison.Ordinal);
        Assert.Contains("set_working_directory", result, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.RecoverableCorrection, context.Receipt?.Category);
        Assert.Equal(ToolOutcomeResults.SetWorkingDirectoryRemediation, context.Receipt?.RemediationCode);
        Assert.Empty(context.Receipt?.FileActivity ?? []);
    }

    [Fact]
    public async Task Set_working_directory_rejects_relative_path_without_state_receipt()
    {
        var context = CreateContext();
        var tool = new SetWorkingDirectoryTool(_config, new NetclawPaths());

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", "other-project"),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("must be absolute", result, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, context.Receipt?.Category);
        Assert.Null(context.Receipt?.DeclaredProjectDirectory);
    }

    [Fact]
    public async Task Set_working_directory_success_reports_validated_project()
    {
        var context = CreateContext();
        var tool = new SetWorkingDirectoryTool(_config, new NetclawPaths());

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", _projectDirectory),
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(_projectDirectory, result);
        Assert.Equal(ToolInvocationOutcomeCategory.Success, context.Receipt?.Category);
        Assert.Equal(_projectDirectory, context.Receipt?.DeclaredProjectDirectory);
    }

    private ToolExecutionContext CreateContext()
        => TestToolExecutionContext.CreateBound(
            "signalr/relative-tools",
            _sessionDirectory,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ProjectDirectory = _projectDirectory
            });

    private static void AssertSuccessfulActivity(
        ToolExecutionContext context,
        string expectedPath,
        ToolFileActivityKind expectedKind)
    {
        Assert.Equal(ToolInvocationOutcomeCategory.Success, context.Receipt?.Category);
        var activity = Assert.Single(context.Receipt?.FileActivity ?? []);
        Assert.Equal(Path.GetFullPath(expectedPath), activity.CanonicalPath);
        Assert.Equal(expectedKind, activity.Kind);
    }
}

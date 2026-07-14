// -----------------------------------------------------------------------
// <copyright file="FileListToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class FileListToolTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FileListTool _tool = new(new ToolConfig(), new NetclawPaths(), new ToolPathPolicy([]));
    private readonly string _sessionDir;

    public FileListToolTests()
    {
        _sessionDir = Path.Combine(_dir.Path, "session");
        Directory.CreateDirectory(_sessionDir);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Team_context_lists_entries_within_session_directory()
    {
        Directory.CreateDirectory(Path.Combine(_sessionDir, "subfolder"));
        await File.WriteAllTextAsync(
            Path.Combine(_sessionDir, "notes.txt"), "data", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            ToolInput.Create("Path", _sessionDir), CreateTeamContext(), CancellationToken.None);

        Assert.Contains("[dir]  subfolder/", result);
        Assert.Contains("[file] notes.txt", result);
        // Read-only: the listed entries are untouched.
        Assert.True(Directory.Exists(Path.Combine(_sessionDir, "subfolder")));
        Assert.True(File.Exists(Path.Combine(_sessionDir, "notes.txt")));
    }

    [Fact]
    public async Task Public_context_can_list_its_session_directory()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_sessionDir, "inbox.txt"), "x", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            ToolInput.Create("Path", _sessionDir), CreatePublicContext(), CancellationToken.None);

        Assert.Contains("inbox.txt", result);
    }

    [Fact]
    public async Task Public_context_cannot_list_directory_outside_session_directory()
    {
        // _dir.Path is the parent of the session directory — outside Public scope.
        var result = await _tool.ExecuteAsync(
            ToolInput.Create("Path", _dir.Path), CreatePublicContext(), CancellationToken.None);

        Assert.Contains("Public trust context", result);
        Assert.Contains("session directory", result);
        // The denial must not disclose configured root paths.
        Assert.DoesNotContain("configured roots", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Team_context_can_list_workspace_directory_via_global_read_roots()
    {
        var paths = new NetclawPaths(_dir.Path);
        Directory.CreateDirectory(Path.Combine(paths.WorkspacesDirectory, "project-a"));
        var tool = new FileListTool(new ToolConfig(), paths, new ToolPathPolicy([]));

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", paths.WorkspacesDirectory), CreateTeamContext(), CancellationToken.None);

        Assert.Contains("[dir]  project-a/", result);
    }

    [Fact]
    public async Task Listing_a_file_path_returns_not_a_directory_error()
    {
        var filePath = Path.Combine(_sessionDir, "a-file.txt");
        await File.WriteAllTextAsync(filePath, "x", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            ToolInput.Create("Path", filePath), CreateTeamContext(), CancellationToken.None);

        Assert.Contains("Not a directory", result);
    }

    [Fact]
    public async Task Missing_directory_returns_error()
    {
        var result = await _tool.ExecuteAsync(
            ToolInput.Create("Path", Path.Combine(_sessionDir, "does-not-exist")),
            CreateTeamContext(),
            CancellationToken.None);

        Assert.Contains("Directory not found", result);
    }

    [Fact]
    public async Task Listing_denied_directory_returns_access_denied()
    {
        var protectedDir = Path.Combine(_sessionDir, "webhooks");
        Directory.CreateDirectory(protectedDir);

        var policy = new ToolPathPolicy([protectedDir]);
        var tool = new FileListTool(new ToolConfig(), new NetclawPaths(), policy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", protectedDir),
            CreatePersonalContext(),
            CancellationToken.None);

        Assert.Contains("Access denied", result);
        Assert.Contains("cannot be read", result);
    }

    [Fact]
    public async Task Listing_filters_denied_child_entries()
    {
        var protectedDir = Path.Combine(_sessionDir, "webhooks");
        var protectedFile = Path.Combine(_sessionDir, "secrets.json");
        var visibleFile = Path.Combine(_sessionDir, "notes.txt");

        Directory.CreateDirectory(protectedDir);
        await File.WriteAllTextAsync(protectedFile, "secret", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(visibleFile, "visible", TestContext.Current.CancellationToken);

        var policy = new ToolPathPolicy([protectedDir], [protectedFile, protectedDir], [protectedDir]);
        var tool = new FileListTool(new ToolConfig(), new NetclawPaths(), policy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", _sessionDir),
            CreatePersonalContext(),
            CancellationToken.None);

        Assert.Contains("notes.txt", result);
        Assert.DoesNotContain("secrets.json", result);
        Assert.DoesNotContain("webhooks", result);
    }

    private ToolExecutionContext CreateTeamContext()
        => TestToolExecutionContext.CreateBound("slack/thread-1", _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = "slack"
        });

    private ToolExecutionContext CreatePublicContext()
        => TestToolExecutionContext.CreateBound("slack/thread-1", _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            ChannelType = "slack"
        });

    private ToolExecutionContext CreatePersonalContext()
        => TestToolExecutionContext.CreateBound("slack/thread-1", _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "slack"
        });
}

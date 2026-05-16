// -----------------------------------------------------------------------
// <copyright file="FileWriteToolTests.cs" company="Petabridge, LLC">
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

public class FileWriteToolTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FileWriteTool _tool = new(new ToolConfig());
    private readonly string _sessionDir;

    public FileWriteToolTests()
    {
        _sessionDir = Path.Combine(_dir.Path, "session");
        Directory.CreateDirectory(_sessionDir);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public async Task Write_new_file_creates_it()
    {
        var filePath = Path.Combine(_dir.Path, "new.txt");
        var args = ToolInput.Create("Path", filePath, "Content", "hello world");

        var result = await _tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Successfully wrote", result);
        Assert.Contains("bytes", result);
        Assert.Equal("hello world", await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Overwrite_existing_file()
    {
        var filePath = Path.Combine(_dir.Path, "existing.txt");
        await File.WriteAllTextAsync(filePath, "old content", TestContext.Current.CancellationToken);

        var args = ToolInput.Create("Path", filePath, "Content", "new content");

        var result = await _tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Successfully wrote", result);
        Assert.Equal("new content", await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Creates_parent_directories()
    {
        var filePath = Path.Combine(_dir.Path, "sub", "dir", "deep.txt");
        var args = ToolInput.Create("Path", filePath, "Content", "deep content");

        var result = await _tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Successfully wrote", result);
        Assert.Equal("deep content", await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_path_returns_error()
    {
        var args = ToolInput.Create("Content", "hello");
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Path", result);
        Assert.Contains("missing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_content_returns_error()
    {
        var args = ToolInput.Create("Path", "/tmp/test.txt");
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Content", result);
        Assert.Contains("missing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_arguments_returns_error()
    {
        var result = await _tool.ExecuteAsync(null, CancellationToken.None);
        Assert.Contains("No arguments provided", result);
    }

    [Fact]
    public async Task Write_denied_path_returns_access_denied()
    {
        var filePath = Path.Combine(_dir.Path, "secrets.json");
        var policy = new ToolPathPolicy([filePath]);
        var tool = new FileWriteTool(new ToolConfig(), policy);

        var args = ToolInput.Create("Path", filePath, "Content", "malicious content");

        var result = await tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Access denied", result);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task Public_context_can_write_inside_session_directory()
    {
        var filePath = Path.Combine(_sessionDir, "note.txt");
        var args = ToolInput.Create("Path", filePath, "Content", "session output");

        var result = await _tool.ExecuteAsync(args, CreatePublicContext(), CancellationToken.None);

        Assert.Contains("Successfully wrote", result);
        Assert.Equal("session output", await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Public_context_cannot_write_outside_session_directory()
    {
        var filePath = Path.Combine(_dir.Path, "host-write.txt");
        var args = ToolInput.Create("Path", filePath, "Content", "blocked");

        var result = await _tool.ExecuteAsync(args, CreatePublicContext(), CancellationToken.None);

        Assert.Contains("Public trust context", result);
        Assert.Contains("session directory", result);
        Assert.False(File.Exists(filePath));
    }

    private ToolExecutionContext CreatePersonalContext()
        => new("signalr/thread-1", _sessionDir)
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr"
        };

    private ToolExecutionContext CreatePublicContext()
        => new("slack/thread-1", _sessionDir)
        {
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            ChannelType = "slack"
        };
}

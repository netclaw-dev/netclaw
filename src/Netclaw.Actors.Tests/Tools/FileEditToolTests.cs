// -----------------------------------------------------------------------
// <copyright file="FileEditToolTests.cs" company="Petabridge, LLC">
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

public class FileEditToolTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FileEditTool _tool = new(new ToolConfig());
    private readonly string _sessionDir;

    public FileEditToolTests()
    {
        _sessionDir = Path.Combine(_dir.Path, "session");
        Directory.CreateDirectory(_sessionDir);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public async Task Edit_single_occurrence_replaces_text()
    {
        var filePath = Path.Combine(_dir.Path, "test.cs");
        await File.WriteAllTextAsync(filePath, "using System;\nusing Xunit;\n\nclass Foo { }", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath, "OldString", "using Xunit;", "NewString", "using NUnit.Framework;"), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Successfully edited", result);
        Assert.Contains("replaced 1 occurrence", result);
        var content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Equal("using System;\nusing NUnit.Framework;\n\nclass Foo { }", content);
    }

    [Fact]
    public async Task ReplaceAll_replaces_all_occurrences()
    {
        var filePath = Path.Combine(_dir.Path, "test.txt");
        await File.WriteAllTextAsync(filePath, "foo bar foo baz foo", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath, "OldString", "foo", "NewString", "qux", "ReplaceAll", true), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("replaced 3 occurrence", result);
        Assert.Equal("qux bar qux baz qux", await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Non_unique_match_without_ReplaceAll_returns_error()
    {
        var filePath = Path.Combine(_dir.Path, "test.txt");
        var original = "foo bar foo baz foo";
        await File.WriteAllTextAsync(filePath, original, TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath, "OldString", "foo", "NewString", "qux"), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("matches 3 locations", result);
        Assert.Equal(original, await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OldString_not_found_returns_error()
    {
        var filePath = Path.Combine(_dir.Path, "test.txt");
        var original = "hello world";
        await File.WriteAllTextAsync(filePath, original, TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath, "OldString", "goodbye", "NewString", "farewell"), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("not found", result);
        Assert.Equal(original, await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OldString_equals_NewString_returns_error()
    {
        var filePath = Path.Combine(_dir.Path, "test.txt");
        await File.WriteAllTextAsync(filePath, "hello world", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath, "OldString", "hello", "NewString", "hello"), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("must be different", result);
    }

    [Fact]
    public async Task Empty_NewString_performs_deletion()
    {
        var filePath = Path.Combine(_dir.Path, "test.cs");
        await File.WriteAllTextAsync(filePath, "line1\nDELETE_ME\nline3", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath, "OldString", "DELETE_ME\n", "NewString", ""), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Successfully edited", result);
        Assert.Equal("line1\nline3", await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task File_does_not_exist_returns_error()
    {
        var filePath = Path.Combine(_dir.Path, "nonexistent.txt");

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath, "OldString", "foo", "NewString", "bar"), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Edit_denied_path_returns_access_denied()
    {
        var filePath = Path.Combine(_dir.Path, "secrets.json");
        await File.WriteAllTextAsync(filePath, "secret data", TestContext.Current.CancellationToken);
        var policy = new ToolPathPolicy([filePath]);
        var tool = new FileEditTool(new ToolConfig(), policy);

        var result = await tool.ExecuteAsync(ToolInput.Create("Path", filePath, "OldString", "secret", "NewString", "public"), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Access denied", result);
        Assert.Equal("secret data", await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Public_context_can_edit_inside_session_directory()
    {
        var filePath = Path.Combine(_sessionDir, "note.txt");
        await File.WriteAllTextAsync(filePath, "old text", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath, "OldString", "old text", "NewString", "new text"), CreatePublicContext(), CancellationToken.None);

        Assert.Contains("Successfully edited", result);
        Assert.Equal("new text", await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Public_context_cannot_edit_outside_session_directory()
    {
        var filePath = Path.Combine(_dir.Path, "host-file.txt");
        await File.WriteAllTextAsync(filePath, "original", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath, "OldString", "original", "NewString", "modified"), CreatePublicContext(), CancellationToken.None);

        Assert.Contains("Public trust context", result);
        Assert.Contains("session directory", result);
        Assert.Equal("original", await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    private ToolExecutionContext CreatePersonalContext()
        => new("signalr/thread-1", _sessionDir)
        {
            Audience = TrustAudience.Personal,
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

    private ToolExecutionContext CreatePublicContext()
        => new("slack/thread-1", _sessionDir)
        {
            Audience = TrustAudience.Public,
            Boundary = SecurityPolicyDefaults.PublicBoundary,
            ChannelType = "slack"
        };
}

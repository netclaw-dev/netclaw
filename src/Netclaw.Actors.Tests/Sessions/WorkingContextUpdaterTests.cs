// -----------------------------------------------------------------------
// <copyright file="WorkingContextUpdaterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public class WorkingContextUpdaterTests
{
    [Fact]
    public void TryExtractFilePath_returns_path_from_path_field()
    {
        var ok = WorkingContextUpdater.TryExtractFilePath(
            """{"path":"src/Rect.cs"}""", out var path);

        Assert.True(ok);
        Assert.Equal("src/Rect.cs", path);
    }

    [Fact]
    public void TryExtractFilePath_returns_path_from_file_path_field()
    {
        var ok = WorkingContextUpdater.TryExtractFilePath(
            """{"file_path":"src/Rect.cs","mode":"r"}""", out var path);

        Assert.True(ok);
        Assert.Equal("src/Rect.cs", path);
    }

    [Fact]
    public void TryExtractFilePath_returns_path_from_camelCase_filePath_field()
    {
        var ok = WorkingContextUpdater.TryExtractFilePath(
            """{"filePath":"src/Rect.cs"}""", out var path);

        Assert.True(ok);
        Assert.Equal("src/Rect.cs", path);
    }

    [Theory]
    [InlineData("Path")]
    [InlineData("FilePath")]
    [InlineData("File")]
    [InlineData("FileName")]
    public void TryExtractFilePath_returns_path_from_PascalCase_field(string fieldName)
    {
        // First-party Netclaw tools (FileReadTool, FileWriteTool, FileEditTool)
        // use PascalCase parameter names via C# records — NetclawToolGenerator
        // emits the schema with those names verbatim, so real arguments are
        // keyed as `{"Path": "..."}`. Missing PascalCase variants would make
        // WorkingContext a no-op for first-party tools.
        var json = $$"""{"{{fieldName}}":"src/Rect.cs"}""";

        var ok = WorkingContextUpdater.TryExtractFilePath(json, out var path);

        Assert.True(ok);
        Assert.Equal("src/Rect.cs", path);
    }

    [Fact]
    public void TryExtractFilePath_returns_false_when_no_path_field_present()
    {
        var ok = WorkingContextUpdater.TryExtractFilePath(
            """{"query":"Rect"}""", out var path);

        Assert.False(ok);
        Assert.Empty(path);
    }

    [Fact]
    public void TryExtractFilePath_returns_false_for_empty_or_null_arguments()
    {
        Assert.False(WorkingContextUpdater.TryExtractFilePath(null, out _));
        Assert.False(WorkingContextUpdater.TryExtractFilePath("", out _));
        Assert.False(WorkingContextUpdater.TryExtractFilePath("{}", out _));
    }

    [Fact]
    public void TryExtractFilePath_returns_false_on_malformed_json()
    {
        Assert.False(WorkingContextUpdater.TryExtractFilePath("not valid json", out _));
        Assert.False(WorkingContextUpdater.TryExtractFilePath("""{"unterminated":""", out _));
    }

    [Fact]
    public void UpdateFromToolResults_pushes_path_for_file_read_tool()
    {
        var history = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.User,
                Content = "Read Rect.cs"
            },
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCalls =
                [
                    new SerializableToolCall
                    {
                        CallId = "call-1",
                        Name = "file_read",
                        ArgumentsJson = """{"path":"src/Rect.cs"}"""
                    }
                ]
            }
        };

        var results = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Tool,
                Name = "file_read",
                ToolCallId = "call-1",
                Content = "file contents..."
            }
        };

        var updated = WorkingContextUpdater.UpdateFromToolResults(
            WorkingContext.Empty, history, results);

        Assert.Equal(new[] { "src/Rect.cs" }, updated.RecentFiles);
    }

    [Fact]
    public void UpdateFromToolResults_ignores_tools_without_path_arguments()
    {
        // shell_execute's args contain `command` but no `path`/`file_path`/
        // etc. — the field-name probe returns no match, so the tool is
        // silently skipped. This works for any tool whose arguments don't
        // look file-path-shaped, regardless of tool name.
        var history = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCalls =
                [
                    new SerializableToolCall
                    {
                        CallId = "call-shell",
                        Name = "shell_execute",
                        ArgumentsJson = """{"command":"ls"}"""
                    }
                ]
            }
        };

        var results = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Tool,
                Name = "shell_execute",
                ToolCallId = "call-shell",
                Content = "a.txt b.txt"
            }
        };

        var updated = WorkingContextUpdater.UpdateFromToolResults(
            WorkingContext.Empty, history, results);

        Assert.Empty(updated.RecentFiles);
    }

    [Fact]
    public void UpdateFromToolResults_ignores_results_without_matching_call()
    {
        var history = new List<SerializableChatMessage>();  // empty history

        var results = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Tool,
                Name = "file_read",
                ToolCallId = "call-orphan",
                Content = "..."
            }
        };

        var updated = WorkingContextUpdater.UpdateFromToolResults(
            WorkingContext.Empty, history, results);

        // Orphan tool result (no matching call in history) — nothing updated
        Assert.Empty(updated.RecentFiles);
    }

    [Fact]
    public void UpdateFromToolResults_dedupes_across_multiple_reads_of_same_file()
    {
        var history = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCalls =
                [
                    new SerializableToolCall
                    {
                        CallId = "call-1",
                        Name = "file_read",
                        ArgumentsJson = """{"path":"src/Rect.cs"}"""
                    },
                    new SerializableToolCall
                    {
                        CallId = "call-2",
                        Name = "file_read",
                        ArgumentsJson = """{"path":"src/Thickness.cs"}"""
                    },
                    new SerializableToolCall
                    {
                        CallId = "call-3",
                        Name = "file_read",
                        ArgumentsJson = """{"path":"src/Rect.cs"}"""
                    }
                ]
            }
        };

        var results = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.Tool, Name = "file_read", ToolCallId = "call-1", Content = "..." },
            new() { Role = ChatRole.Tool, Name = "file_read", ToolCallId = "call-2", Content = "..." },
            new() { Role = ChatRole.Tool, Name = "file_read", ToolCallId = "call-3", Content = "..." }
        };

        var updated = WorkingContextUpdater.UpdateFromToolResults(
            WorkingContext.Empty, history, results);

        // call-3 re-reads Rect.cs, moving it back to front. Dedupe means
        // only one entry for Rect.cs.
        Assert.Equal(2, updated.RecentFiles.Count);
        Assert.Equal("src/Rect.cs", updated.RecentFiles[0]);
        Assert.Equal("src/Thickness.cs", updated.RecentFiles[1]);
    }
}

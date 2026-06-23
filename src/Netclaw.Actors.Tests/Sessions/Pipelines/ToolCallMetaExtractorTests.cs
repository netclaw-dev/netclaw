// -----------------------------------------------------------------------
// <copyright file="ToolCallMetaExtractorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

public class ToolCallMetaExtractorTests
{
    [Fact]
    public void Extract_WithAllMetaFields_ReturnsMetaAndCleanArgs()
    {
        var args = new Dictionary<string, object?>
        {
            ["Command"] = "dotnet test",
            ["_rationale"] = "running tests",
            ["_timeout_seconds"] = 300,
            ["_background"] = true
        };
        var tc = new FunctionCallContent("call-1", "shell_execute", args);

        var (meta, cleaned) = ToolCallMetaExtractor.Extract(tc);

        Assert.NotNull(meta);
        Assert.Equal("running tests", meta!.Rationale);
        Assert.Equal(300, meta.TimeoutHintSeconds);
        Assert.True(meta.Background);

        Assert.Contains("Command", (IDictionary<string, object?>)cleaned.Arguments!);
        Assert.DoesNotContain("_rationale", (IDictionary<string, object?>)cleaned.Arguments!);
        Assert.DoesNotContain("_timeout_seconds", (IDictionary<string, object?>)cleaned.Arguments!);
        Assert.DoesNotContain("_background", (IDictionary<string, object?>)cleaned.Arguments!);
    }

    [Fact]
    public void Extract_WithOnlyRationale_ReturnsMetaWithRationaleOnly()
    {
        var args = new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["_rationale"] = "searching docs"
        };
        var tc = new FunctionCallContent("call-1", "web_search", args);

        var (meta, cleaned) = ToolCallMetaExtractor.Extract(tc);

        Assert.NotNull(meta);
        Assert.Equal("searching docs", meta!.Rationale);
        Assert.Null(meta.TimeoutHintSeconds);
        Assert.False(meta.Background);
    }

    [Fact]
    public void Extract_WithNoMetaFields_ReturnsNullMeta()
    {
        var args = new Dictionary<string, object?>
        {
            ["Command"] = "ls"
        };
        var tc = new FunctionCallContent("call-1", "shell_execute", args);

        var (meta, cleaned) = ToolCallMetaExtractor.Extract(tc);

        Assert.Null(meta);
        Assert.Same(tc, cleaned);
    }

    [Fact]
    public void Extract_WithNullArguments_ReturnsNullMeta()
    {
        var tc = new FunctionCallContent("call-1", "shell_execute", null);

        var (meta, cleaned) = ToolCallMetaExtractor.Extract(tc);

        Assert.Null(meta);
        Assert.Same(tc, cleaned);
    }

    [Fact]
    public void Extract_HandlesJsonElementValues()
    {
        var jsonDoc = JsonDocument.Parse("""{"_rationale":"from json","_timeout_seconds":120,"_background":true,"Command":"ls"}""");
        var args = new Dictionary<string, object?>();
        foreach (var prop in jsonDoc.RootElement.EnumerateObject())
            args[prop.Name] = prop.Value;

        var tc = new FunctionCallContent("call-1", "shell_execute", args);
        var (meta, cleaned) = ToolCallMetaExtractor.Extract(tc);

        Assert.NotNull(meta);
        Assert.Equal("from json", meta!.Rationale);
        Assert.Equal(120, meta.TimeoutHintSeconds);
        Assert.True(meta.Background);
    }

    [Fact]
    public void Extract_TimeoutSeconds_ZeroNotTreatedAsMeta()
    {
        var args = new Dictionary<string, object?>
        {
            ["Command"] = "ls",
            ["_timeout_seconds"] = 0
        };
        var tc = new FunctionCallContent("call-1", "shell_execute", args);

        var (meta, _) = ToolCallMetaExtractor.Extract(tc);

        // Zero timeout is not meaningful — should not produce meta
        Assert.Null(meta);
    }

    // Timeout-hint application (honor-or-default; no clamp/floor) is exercised
    // end-to-end in MetaValidationAndNoticeTests against the pipeline. ExtractFrom's
    // own parsing of _timeout_seconds (positive int → hint; else null) is covered
    // by the extraction tests above and ToolArgumentHelperStrictTests.

    // ── Background signaling tests ──

    [Fact]
    public void Extract_TimeoutAlone_DoesNotTriggerBackground()
    {
        var args = new Dictionary<string, object?>
        {
            ["Command"] = "dotnet build",
            ["_rationale"] = "building project",
            ["_timeout_seconds"] = 300
        };
        var tc = new FunctionCallContent("call-1", "shell_execute", args);

        var (meta, _) = ToolCallMetaExtractor.Extract(tc);

        Assert.NotNull(meta);
        Assert.False(meta!.Background);
    }

    [Fact]
    public void Extract_BackgroundFalse_DoesNotSetBackground()
    {
        var args = new Dictionary<string, object?>
        {
            ["Command"] = "dotnet build",
            ["_rationale"] = "building",
            ["_background"] = false
        };
        var tc = new FunctionCallContent("call-1", "shell_execute", args);

        var (meta, _) = ToolCallMetaExtractor.Extract(tc);

        Assert.NotNull(meta);
        Assert.False(meta!.Background);
    }
}
